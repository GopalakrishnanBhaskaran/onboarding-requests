using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using PmoReview.Core.Common;
using PmoReview.Core.Security;

namespace PmoReview.Core.Http;

/// <summary>Transport-neutral request. The Functions layer copies HttpRequestData into this.</summary>
public sealed record ApiRequest(
    string Method,
    string Path,
    IReadOnlyDictionary<string, string> Headers,
    string? Body,
    string CorrelationId)
{
    public string? Header(string name) =>
        Headers.FirstOrDefault(h => string.Equals(h.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
}

public sealed record ApiResponse(int Status, object? Body, IReadOnlyDictionary<string, string>? Headers = null)
{
    public string? SerializedBody => Body is null ? null : AppJson.Serialize(Body);
}

public sealed record ApiError(string Code, string Message, string CorrelationId, IReadOnlyList<string>? Details = null);

public sealed record RouteContext(
    ApiRequest Request,
    CallerContext Caller,
    IReadOnlyDictionary<string, string> Values,
    CancellationToken Ct)
{
    public string this[string name] => Values[name];

    /// <summary>Deserialises the body into a DTO. Unknown fields (e.g. forged projectId, createdBy, result) are ignored.</summary>
    public T Body<T>() where T : class
    {
        if (string.IsNullOrWhiteSpace(Request.Body)) throw AppException.Invalid("Request body is required.");
        try
        {
            return AppJson.Deserialize<T>(Request.Body) ?? throw AppException.Invalid("Request body is required.");
        }
        catch (JsonException)
        {
            throw AppException.Invalid("Request body is not valid JSON for this operation.");
        }
    }

    public string? IfMatch => Request.Header("If-Match")?.Trim();

    /// <summary>Query string values by name (case-insensitive name; repeated names keep every value, in order).</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Query { get; init; } = new Dictionary<string, IReadOnlyList<string>>();

    public string? QueryValue(string name) => Query.TryGetValue(name, out var v) && v.Count > 0 ? v[0] : null;

    public IReadOnlyList<string> QueryValues(string name) => Query.TryGetValue(name, out var v) ? v : [];
}

public delegate Task<ApiResponse> RouteHandler(RouteContext ctx);

/// <summary>
/// Single entry point for every API call: authenticate from the platform header, resolve grants,
/// route, and map failures to safe responses. Handlers do their own per-project authorisation.
/// </summary>
public sealed partial class ApiHost(AccessService access, IReadOnlyCollection<string> allowedProviders, ILogger<ApiHost> logger)
{
    public const int MaxBodyBytes = 64 * 1024;
    private readonly List<(string Method, string[] Segments, RouteHandler Handler)> _routes = [];

    /// <summary>"METHOD template" for every mapped route. Used by acceptance gates (no fulfilment routes).</summary>
    public IReadOnlyList<string> Routes => _routes.Select(r => $"{r.Method} {string.Join('/', r.Segments)}").ToList();

    public ApiHost Map(string method, string template, RouteHandler handler)
    {
        _routes.Add((method.ToUpperInvariant(), template.Trim('/').Split('/'), handler));
        return this;
    }

    public async Task<ApiResponse> HandleAsync(ApiRequest request, CancellationToken ct)
    {
        try
        {
            if (request.Body is { Length: > MaxBodyBytes })
                throw new AppException(ErrorKind.PayloadTooLarge, "payload_too_large", "Request body is too large.");

            var principal = ClientPrincipalParser.Parse(request.Header(ClientPrincipalParser.HeaderName), allowedProviders);
            var caller = await access.ResolveAsync(principal, ct);

            var (pathOnly, query) = SplitQuery(request.Path);
            var path = pathOnly.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            var pathMatched = false;
            foreach (var (method, segments, handler) in _routes)
            {
                if (!TryMatch(segments, path, out var values)) continue;
                pathMatched = true;
                if (method != request.Method.ToUpperInvariant()) continue;

                var response = await handler(new RouteContext(request, caller, values, ct) { Query = query });
                logger.LogInformation("api {Method} {Template} -> {Status} cid={CorrelationId}",
                    method, string.Join('/', segments), response.Status, request.CorrelationId);
                return response;
            }
            return Error(pathMatched ? 405 : 404, pathMatched ? "method_not_allowed" : "not_found",
                pathMatched ? "Method not allowed." : "Not found.", request.CorrelationId);
        }
        catch (AppException e)
        {
            logger.LogInformation("api {Method} rejected {Code} cid={CorrelationId}", request.Method, e.Code, request.CorrelationId);
            return Error(StatusFor(e.Kind), e.Code, e.Message, request.CorrelationId, e.Details);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Never echo exception details to the caller.
            logger.LogError("api {Method} failed {ExceptionType} cid={CorrelationId}", request.Method, e.GetType().Name, request.CorrelationId);
            return Error(500, "internal_error", "Something went wrong. Quote the correlation id if you report this.", request.CorrelationId);
        }
    }

    public static int StatusFor(ErrorKind kind) => kind switch
    {
        ErrorKind.Unauthenticated => 401,
        ErrorKind.Forbidden => 403,
        ErrorKind.NotFound => 404,
        ErrorKind.Validation => 400,
        ErrorKind.Conflict => 409,
        ErrorKind.PreconditionFailed => 412,
        ErrorKind.PreconditionRequired => 428,
        ErrorKind.PayloadTooLarge => 413,
        _ => 500,
    };

    private static ApiResponse Error(int status, string code, string message, string cid, IReadOnlyList<string>? details = null) =>
        new(status, new { error = new ApiError(code, message, cid, details) });

    /// <summary>"path?a=1&b=2&b=3" → the path and the decoded query values (at most 64 parameters, 2,000 characters each).</summary>
    public static (string Path, IReadOnlyDictionary<string, IReadOnlyList<string>> Query) SplitQuery(string pathAndQuery)
    {
        var at = pathAndQuery.IndexOf('?');
        var query = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        if (at < 0) return (pathAndQuery, query);
        var lists = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in pathAndQuery[(at + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries).Take(64))
        {
            var eq = part.IndexOf('=');
            var name = Uri.UnescapeDataString((eq < 0 ? part : part[..eq]).Replace('+', ' '));
            var value = eq < 0 ? "" : Uri.UnescapeDataString(part[(eq + 1)..].Replace('+', ' '));
            if (name.Length == 0 || value.Length > 2000) continue;
            if (!lists.TryGetValue(name, out var list)) lists[name] = list = [];
            list.Add(value);
        }
        foreach (var (k, v) in lists) query[k] = v;
        return (pathAndQuery[..at], query);
    }

    private static bool TryMatch(string[] template, string[] path, out Dictionary<string, string> values)
    {
        values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (template.Length != path.Length) return false;
        for (var i = 0; i < template.Length; i++)
        {
            var t = template[i];
            if (t.StartsWith('{') && t.EndsWith('}'))
            {
                var value = Uri.UnescapeDataString(path[i]);
                if (!SafeId().IsMatch(value)) return false;
                values[t[1..^1]] = value;
            }
            else if (!string.Equals(t, path[i], StringComparison.Ordinal))
            {
                return false;
            }
        }
        return true;
    }

    [GeneratedRegex("^[A-Za-z0-9_.:\\-]{1,128}$")]
    private static partial Regex SafeId();
}
