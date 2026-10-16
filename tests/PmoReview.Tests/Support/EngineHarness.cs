using System.Net;
using System.Text;
using PmoReview.Adapters.Review;
using PmoReview.Adapters.Storage;
using PmoReview.Core.Domain;
using PmoReview.Core.Review;
using PmoReview.Core.Storage;

namespace PmoReview.Tests.Support;

public static class EngineHarness
{
    public static readonly DateTimeOffset Now = new(2026, 10, 16, 9, 0, 0, TimeSpan.Zero);

    public static DataStores Stores()
    {
        var stores = InMemoryDataStores.Create();
        SampleDataLoader.SeedAsync(stores, SampleDataLoader.Read(Path.Combine(AppContext.BaseDirectory, "sample-data.json"))).GetAwaiter().GetResult();
        return stores;
    }

    /// <summary>A complete, valid SAMPLE-ALPHA NewJoiner request for Dev.</summary>
    public static RequestSnapshot Complete(Func<RequestSnapshot, RequestSnapshot>? change = null)
    {
        var s = new RequestSnapshot
        {
            RequestId = "req-1",
            ProjectId = TestApp.Alpha,
            Revision = 1,
            RequestType = "NewJoiner",
            Role = "SAMPLE Data Engineer",
            Environment = "Dev",
            Manager = "SAMPLE Manager One",
            Justification = "SAMPLE: joins the data platform team to build ingestion pipelines.",
            StartDate = new DateOnly(2026, 11, 2),
            EndDate = new DateOnly(2027, 3, 31),
            CapturedAt = Now,
        };
        return change is null ? s : change(s);
    }

    public static Task<EngineOutcome> Run(RequestSnapshot s, IReviewModel? model = null, ReviewLimits? limits = null, DataStores? stores = null) =>
        new ReviewEngine(stores ?? Stores(), model ?? new RuleOnlyReviewModel(), limits ?? new ReviewLimits())
            .RunAsync(s, "run-1", CancellationToken.None);
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
        Requests.Add((request, await request.Content!.ReadAsStringAsync(ct)));
        if (_next < responses.Length) return responses[_next++]();
        return Repeat?.Invoke() ?? throw new InvalidOperationException("No more scripted responses.");
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage ToolCall(string name, string argumentsJson, string id = "call_1") =>
        Json(System.Text.Json.JsonSerializer.Serialize(new
        {
            choices = new[]
            {
                new
                {
                    finish_reason = "tool_calls",
                    message = new
                    {
                        role = "assistant",
                        content = (string?)null,
                        tool_calls = new[] { new { id, type = "function", function = new { name, arguments = argumentsJson } } },
                    },
                },
            },
        }));

    public static HttpResponseMessage Final(string contentJson) =>
        Json(System.Text.Json.JsonSerializer.Serialize(new
        {
            choices = new[] { new { finish_reason = "stop", message = new { role = "assistant", content = contentJson } } },
        }));

    public static AzureOpenAIReviewModel Model(ScriptedHandler handler) =>
        new(new HttpClient(handler), new AzureOpenAIOptions { Endpoint = new Uri("https://sample-aoai.invalid/"), Deployment = "sample-deployment" },
            _ => ValueTask.FromResult("sample-token"));
}
