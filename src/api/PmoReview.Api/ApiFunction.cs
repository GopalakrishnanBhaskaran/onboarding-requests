using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using PmoReview.Core.Http;

namespace PmoReview.Api;

/// <summary>
/// One HTTP trigger for every route. Routing, authentication (from x-ms-client-principal) and
/// authorisation all happen in <see cref="ApiHost"/>, which is unit-tested without the Functions host.
/// </summary>
public sealed partial class ApiFunction(ApiHost api)
{
    [Function("api")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post", "put", Route = "{*path}")] HttpRequestData req,
        string? path,
        CancellationToken ct)
    {
        var headers = req.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        var body = await ReadBodyAsync(req.Body, ct);
        var cid = headers.TryGetValue("x-correlation-id", out var c) && SafeCid().IsMatch(c) ? c : Guid.NewGuid().ToString("n");

        var result = await api.HandleAsync(new ApiRequest(req.Method, (path ?? "") + req.Url.Query, headers, body, cid), ct);

        var res = req.CreateResponse((HttpStatusCode)result.Status);
        res.Headers.Add("x-correlation-id", cid);
        res.Headers.Add("Cache-Control", "no-store");
        res.Headers.Add("X-Content-Type-Options", "nosniff");
        foreach (var (k, v) in result.Headers ?? new Dictionary<string, string>()) res.Headers.Add(k, v);
        if (result.SerializedBody is { } json)
        {
            res.Headers.Add("Content-Type", "application/json; charset=utf-8");
            await res.WriteStringAsync(json, ct);
        }
        return res;
    }

    /// <summary>Reads at most one byte more than the limit so ApiHost can reject oversized bodies.</summary>
    private static async Task<string?> ReadBodyAsync(Stream body, CancellationToken ct)
    {
        using var reader = new StreamReader(body);
        var buffer = new char[ApiHost.MaxBodyBytes + 1];
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await reader.ReadAsync(buffer.AsMemory(read), ct);
            if (n == 0) break;
            read += n;
        }
        return read == 0 ? null : new string(buffer, 0, read);
    }

    [GeneratedRegex("^[A-Za-z0-9-]{1,64}$")]
    private static partial Regex SafeCid();
}
