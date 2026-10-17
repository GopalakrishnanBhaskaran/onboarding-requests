using System.Net;
using System.Text;
using PmoReview.Adapters.Review;
using PmoReview.Adapters.Storage;
using PmoReview.Core.Directory;
using PmoReview.Core.Domain;
using PmoReview.Core.Review;
using PmoReview.Core.Sources;
using PmoReview.Core.Storage;

namespace PmoReview.Tests.Support;

public static class EngineHarness
{
    public static readonly DateTimeOffset Now = new(2026, 10, 17, 9, 0, 0, TimeSpan.Zero);

    public static DataStores Stores()
    {
        var stores = InMemoryDataStores.Create();
        SampleDataLoader.SeedAsync(stores, SampleDataLoader.Read(Path.Combine(AppContext.BaseDirectory, "sample-data.json"))).GetAwaiter().GetResult();
        return stores;
    }

    /// <summary>A complete SAMPLE-ALPHA request that maps to row A-DBW-ENG-USER.</summary>
    public static RequestSnapshot Complete(Func<RequestSnapshot, RequestSnapshot>? change = null)
    {
        var s = new RequestSnapshot
        {
            RequestId = "req-1",
            ProjectId = TestApp.Alpha,
            Revision = 1,
            EnterpriseId = "sample.e1001",
            FirstName = "Sample",
            LastName = "Joiner",
            DomainId = TestApp.DefaultUser,
            Resource = "SAMPLE Databricks Engineering",
            AccessLevel = "User",
            Justification = "SAMPLE: joins the data engineering team to build and run ingestion pipelines in the dev workspace.",
            Owner = "sample-user-olivia@sample.invalid",
            CapturedAt = Now,
        };
        return change is null ? s : change(s);
    }

    public static Task<EngineOutcome> Run(RequestSnapshot s, IReviewModel? model = null, ReviewLimits? limits = null, DataStores? stores = null,
        IDirectoryReader? directory = null, bool useDirectory = true)
    {
        var st = stores ?? Stores();
        return new ReviewEngine(st, new StoreSourcesOfTruth(st), model ?? new RuleOnlyReviewModel(), limits ?? new ReviewLimits(), new ReviewPolicy(),
                useDirectory ? directory ?? FakeDirectory.SeededFromSample() : null)
            .RunAsync(s, "run-1", CancellationToken.None);
    }

    public static CheckResult Check(this EngineOutcome o, string code) => o.Result.Checks.Single(c => c.Code == code);
}

/// <summary>Scripted reviewer for host-validation tests.</summary>
public sealed class FakeModel(Func<ReviewContext, CancellationToken, Task<ModelReview>> review) : IReviewModel
{
    public ReviewerInfo Info { get; } = new("Fake", "0", "fake-v0");

    public Task<ModelReview> ReviewAsync(ReviewContext ctx, ReviewLimits limits, CancellationToken ct) => review(ctx, ct);

    public static FakeModel Returning(ModelReview r) => new((_, _) => Task.FromResult(r));
}

/// <summary>Mocked Azure OpenAI endpoint: replays scripted responses and records requests.</summary>
public sealed class ScriptedHandler(params Func<HttpResponseMessage>[] responses) : HttpMessageHandler
{
    private int _next;
    public List<(HttpRequestMessage Request, string Body)> Requests { get; } = [];

    public Func<HttpResponseMessage>? Repeat { get; init; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add((request, request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct)));
        if (_next < responses.Length) return responses[_next++]();
        return Repeat?.Invoke() ?? throw new InvalidOperationException("No more scripted responses.");
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Final(string contentJson, int promptTokens = 120, int completionTokens = 30) =>
        Json(System.Text.Json.JsonSerializer.Serialize(new
        {
            choices = new[] { new { finish_reason = "stop", message = new { role = "assistant", content = contentJson } } },
            usage = new { prompt_tokens = promptTokens, completion_tokens = completionTokens },
        }));

    public static HttpResponseMessage Throttled(int retryAfterMs = 10)
    {
        var r = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        r.Headers.Add("retry-after-ms", retryAfterMs.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return r;
    }

    public static AzureOpenAIChat Chat(ScriptedHandler handler) =>
        new(new HttpClient(handler), new AzureOpenAIOptions { Endpoint = new Uri("https://sample-aoai.invalid/"), Deployment = "sample-deployment" },
            _ => ValueTask.FromResult("sample-token"));

    public static AzureOpenAIReviewModel Model(ScriptedHandler handler) => new(Chat(handler));
}
