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
        var bytes = await ReadBodyAsync(req.Body, ct);
        var cid = headers.TryGetValue("x-correlation-id", out var c) && SafeCid().IsMatch(c) ? c : Guid.NewGuid().ToString("n");

        // JSON routes read Body (text); file uploads (F4) read RawBody. ApiHost refuses a body over the route's limit.
        var body = bytes.Length == 0 || bytes.Length > ApiHost.MaxBodyBytes + 1 ? null : System.Text.Encoding.UTF8.GetString(bytes);
        var result = await api.HandleAsync(new ApiRequest(req.Method, (path ?? "") + req.Url.Query, headers, body, cid) { RawBody = bytes.Length == 0 ? null : bytes }, ct);

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

    /// <summary>Reads at most one byte more than the largest route limit so ApiHost can reject oversized bodies.</summary>
    private static async Task<byte[]> ReadBodyAsync(Stream body, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int n;
        while (buffer.Length <= ApiHost.MaxUploadBodyBytes && (n = await body.ReadAsync(chunk, ct)) > 0)
            buffer.Write(chunk, 0, (int)Math.Min(n, ApiHost.MaxUploadBodyBytes + 1 - buffer.Length));
        return buffer.ToArray();
    }

    [GeneratedRegex("^[A-Za-z0-9-]{1,64}$")]
    private static partial Regex SafeCid();
}
