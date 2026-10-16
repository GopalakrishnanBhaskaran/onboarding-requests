using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PmoReview.Adapters;
using PmoReview.Adapters.Storage;
using PmoReview.Core.Common;
using PmoReview.Core.Http;
using PmoReview.Core.Security;
using PmoReview.Core.Storage;

namespace PmoReview.Tests.Support;

public sealed class FixedClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset UtcNow { get; set; } = now;
    public void Advance(TimeSpan by) => UtcNow += by;
}

/// <summary>Seeded SAMPLE dataset (in-memory) behind the real API host, called the way SWA calls it.</summary>
public sealed class TestApp
{
    public const string Alpha = "SAMPLE-ALPHA";
    public const string Beta = "SAMPLE-BETA";
    public const string Alice = "sample-user-alice"; // PMO, Alpha
    public const string Rita = "sample-user-rita";   // Reviewer, Alpha
    public const string Mark = "sample-user-mark";   // Maintainer, Alpha
    public const string Oscar = "sample-user-oscar"; // Operator, Alpha
    public const string Bob = "sample-user-bob";     // PMO, Beta
    public const string Bella = "sample-user-bella"; // Reviewer + Maintainer, Beta
    public const string Stranger = "sample-user-nobody"; // signed in, no access rows

    public DataStores Stores { get; }
    public FixedClock Clock { get; } = new(new DateTimeOffset(2026, 10, 16, 9, 0, 0, TimeSpan.Zero));
    public ApiHost Host { get; private set; }

    private TestApp(DataStores stores, Func<TestApp, ApiHost> build)
    {
        Stores = stores;
        Host = build(this);
    }

    public static TestApp Create(Func<DataStores, IClock, ApiHost>? build = null)
    {
        var stores = InMemoryDataStores.Create();
        var data = SampleDataLoader.Read(Path.Combine(AppContext.BaseDirectory, "sample-data.json"));
        SampleDataLoader.SeedAsync(stores, data).GetAwaiter().GetResult();
        return new TestApp(stores, app => build?.Invoke(stores, app.Clock)
            ?? PilotApp.Build(stores, new PilotSettings(), app.Clock, NullLoggerFactory.Instance));
    }

    public static string Principal(string userId, string provider = "aad", params string[] roles) =>
        ClientPrincipalParser.Encode(new ClientPrincipal(provider, userId, $"{userId}@sample.invalid",
            roles.Length == 0 ? ["anonymous", "authenticated"] : roles));

    public Task<Resp> Call(string? user, string method, string path, object? body = null, string? ifMatch = null, string? rawBody = null)
    {
        var headers = new Dictionary<string, string>();
        if (user is not null) headers[ClientPrincipalParser.HeaderName] = Principal(user);
        if (ifMatch is not null) headers["If-Match"] = ifMatch;
        return Send(method, path, headers, rawBody ?? (body is null ? null : AppJson.Serialize(body)));
    }

    public async Task<Resp> Send(string method, string path, Dictionary<string, string> headers, string? body)
    {
        var r = await Host.HandleAsync(new ApiRequest(method, path, headers, body, Guid.NewGuid().ToString("n")), CancellationToken.None);
        return new Resp(r.Status, r.SerializedBody, r.Headers);
    }
}

public sealed record Resp(int Status, string? Body, IReadOnlyDictionary<string, string>? Headers)
{
    public JsonElement Json => JsonDocument.Parse(Body ?? "null").RootElement;
    public JsonElement Value => Json.GetProperty("value");
    public string ETag => Json.GetProperty("etag").GetString()!;
    public string ErrorCode => Json.GetProperty("error").GetProperty("code").GetString()!;
}
