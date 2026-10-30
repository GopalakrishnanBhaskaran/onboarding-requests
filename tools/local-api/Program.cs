using PmoReview.Adapters;
using PmoReview.Adapters.Storage;
using PmoReview.Core.Common;
using PmoReview.Core.Http;

// DEV ONLY: loopback, in-memory SAMPLE data, refuses to start without PILOT_LOCAL_DEV=true.
// It trusts x-ms-client-principal exactly like SWA managed Functions do, so it must only ever sit behind the SWA CLI emulator.
if (!string.Equals(Environment.GetEnvironmentVariable("PILOT_LOCAL_DEV"), "true", StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine("Refusing to start: set PILOT_LOCAL_DEV=true. This host is for local development only.");
    return 1;
}

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:7071");
var app = builder.Build();

var stores = InMemoryDataStores.Create();
// The SAMPLE rows' Azure roles use a placeholder resource group scope locally (no Azure here).
var accessScope = Environment.GetEnvironmentVariable("SAMPLE_ACCESS_SCOPE") ?? "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-pmo-sample-access";
await SampleDataLoader.SeedAsync(stores, SampleDataLoader.Read(Path.Combine(AppContext.BaseDirectory, "sample-data.json"), accessScope));
var settings = PilotSettings.FromEnvironment(Environment.GetEnvironmentVariable);
var api = PilotApp.Build(stores, settings, new SystemClock(), app.Services.GetRequiredService<ILoggerFactory>());

app.Map("/api/{**path}", async (HttpContext ctx, string? path) =>
{
    var headers = ctx.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase);
    using var reader = new StreamReader(ctx.Request.Body);
    var body = await reader.ReadToEndAsync();
    var result = await api.HandleAsync(new ApiRequest(ctx.Request.Method, (path ?? "") + ctx.Request.QueryString, headers, body.Length == 0 ? null : body, Guid.NewGuid().ToString("n")), ctx.RequestAborted);

    ctx.Response.StatusCode = result.Status;
    ctx.Response.Headers.CacheControl = "no-store";
    foreach (var (k, v) in result.Headers ?? new Dictionary<string, string>()) ctx.Response.Headers[k] = v;
    if (result.SerializedBody is { } json)
    {
        ctx.Response.ContentType = "application/json; charset=utf-8";
        await ctx.Response.WriteAsync(json);
    }
});

app.Logger.LogWarning("Local dev API on http://127.0.0.1:7071 with in-memory SAMPLE data (reviewer {Reviewer}).", settings.ReviewModelProvider);
await app.RunAsync();
return 0;
