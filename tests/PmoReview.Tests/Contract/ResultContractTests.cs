using System.Text.Json;
using System.Text.Json.Nodes;
using PmoReview.Adapters;
using PmoReview.Adapters.Review;
using PmoReview.Core.Common;
using PmoReview.Core.Review;
using PmoReview.Core.Workflow;
using PmoReview.Tests.Support;
using Xunit;

namespace PmoReview.Tests.Contract;

public class ResultContractTests
{
    private static async Task<JsonObject> ValidResult() =>
        JsonNode.Parse(AppJson.Serialize((await EngineHarness.Run(EngineHarness.Complete())).Result))!.AsObject();

    [Fact]
    public async Task Valid_result_passes()
    {
        Assert.Empty(ResultSchema.Validate((await ValidResult()).ToJsonString()));
    }

    [Theory]
    [InlineData("outcome", "\"Approved\"")]
    [InlineData("humanApprovalRequired", "false")]
    [InlineData("schemaVersion", "\"1.0\"")]
    [InlineData("approved", "true")]
    [InlineData("assignRole", "{\"group\":\"x\"}")]
    public async Task Tampered_result_fails(string property, string json)
    {
        var r = await ValidResult();
        r[property] = JsonNode.Parse(json);
        Assert.NotEmpty(ResultSchema.Validate(r.ToJsonString()));
    }

    [Fact]
    public async Task Missing_checks_fails()
    {
        var r = await ValidResult();
        r.Remove("checks");
        Assert.NotEmpty(ResultSchema.Validate(r.ToJsonString()));
    }

    /// <summary>Same fixtures as tools/validate_result_schema.py, so C# and Python validators agree.</summary>
    [Fact]
    public void Shared_fixtures_agree_with_python_validator()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "fixtures");
        Assert.Empty(ResultSchema.Validate(File.ReadAllText(Path.Combine(dir, "valid-result.json"))));
        var invalid = Directory.GetFiles(dir, "invalid-*.json");
        Assert.NotEmpty(invalid);
        Assert.All(invalid, f => Assert.NotEmpty(ResultSchema.Validate(File.ReadAllText(f))));
    }

    [Fact]
    public void Paid_reviewer_is_refused_without_explicit_flag_and_builds_when_allowed()
    {
        var paid = new PilotSettings { ReviewModelProvider = "AzureOpenAI", AzureOpenAIEndpoint = new Uri("https://sample-aoai.invalid/"), AzureOpenAIDeployment = "sample" };
        Assert.Contains("ALLOW_PAID_SERVICES", Assert.Throws<InvalidOperationException>(() => ReviewModelFactory.Create(paid)).Message);
        Assert.Equal("AzureOpenAI", ReviewModelFactory.Create(paid with { AllowPaidServices = true }).Info.Provider);
        Assert.IsType<RuleOnlyReviewModel>(ReviewModelFactory.Create(new PilotSettings()));
        Assert.Throws<InvalidOperationException>(() => ReviewModelFactory.Create(new PilotSettings { ReviewModelProvider = "Other" }));
    }
}

/// <summary>Flipping provider flags (from config/*.json) proves each paid path compiles and passes the same contract with mocks.</summary>
public class PromotionPathTests
{
    private static Func<string, string?> Settings(string file, Dictionary<string, string>? overrides = null)
    {
        var values = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(GraphDirectoryReaderTests.Repo(), "config", file)))!;
        foreach (var (k, v) in overrides ?? []) values[k] = v;
        return k => values.GetValueOrDefault(k);
    }

    [Fact]
    public async Task Pilot_settings_build_the_zero_cost_path()
    {
        var settings = PilotSettings.FromEnvironment(Settings("pilot.settings.json"));
        Assert.False(settings.AllowPaidServices);
        Assert.Equal("RuleOnly", settings.ReviewModelProvider);
        Assert.Equal("Inline", settings.Dispatcher);
        Assert.Equal(60, settings.Workflow.TapMaxLifetimeMinutes);
        var app = TestApp.With(settings: settings);
        var id = await app.SubmitId();
        Assert.Equal(RequestStatus.AiReview, await app.Status(id));
        await app.RunWorker();
        Assert.Equal(RequestStatus.AwaitingOwner, await app.Status(id));
    }

    [Fact]
    public void Paid_template_is_refused_if_paid_flag_is_flipped_back()
    {
        var settings = PilotSettings.FromEnvironment(Settings("paid.settings.template.json", new() { ["ALLOW_PAID_SERVICES"] = "false" }));
        Assert.Throws<InvalidOperationException>(() => ReviewModelFactory.Create(settings));
    }

    [Fact]
    public async Task Paid_template_runs_queue_and_azure_openai_paths_with_mocks()
    {
        var settings = PilotSettings.FromEnvironment(Settings("paid.settings.template.json", new()
        {
            ["AZURE_OPENAI_ENDPOINT"] = "https://sample-aoai.invalid/",
            ["AZURE_OPENAI_DEPLOYMENT"] = "sample-deployment",
            ["AZURE_OPENAI_API_VERSION"] = "2024-10-21",
        }));
        Assert.True(settings.AllowPaidServices);
        Assert.True(settings.AiReports);
        Assert.Equal("AzureOpenAI", ReviewModelFactory.Create(settings).Info.Provider);

        var handler = new ScriptedHandler { Repeat = () => ScriptedHandler.Final("""{"summary":"Fine.","recommendation":"Pass","findings":[],"limitations":[]}""") };
        var model = ScriptedHandler.Model(handler);
        var queue = new ListQueue();
        var app = TestApp.With(model, settings, queue);
        var id = await app.SubmitId();
        var run = (await app.Stores.Runs.ListAsync(TestApp.Alpha)).Single(r => r.Value.RequestId == id).Value;
        Assert.Equal(PmoReview.Core.Domain.ProcessingStatus.Queued, run.ProcessingStatus); // returns immediately in the paid design
        Assert.True(await new PmoReview.Adapters.Dispatch.QueueMessageHandler(app.Api.Processor).HandleAsync(Assert.Single(queue.Messages)));
        run = (await app.Stores.Runs.GetAsync(TestApp.Alpha, run.Id))!.Value;
        Assert.Equal("AzureOpenAI", run.ReviewerProvider);
        Assert.Equal("aoai-review-v2", run.PromptVersion);
        Assert.Empty(ResultSchema.Validate(AppJson.Serialize(run.Result)));
        await app.RunWorker(); // Entra cross-check on the worker
        Assert.Equal(RequestStatus.AwaitingOwner, await app.Status(id));
    }

    private sealed class ListQueue : PmoReview.Adapters.Dispatch.IQueueClient
    {
        public List<string> Messages { get; } = [];
        public Task SendAsync(string message, CancellationToken ct)
        {
            Messages.Add(message);
            return Task.CompletedTask;
        }
    }
}
