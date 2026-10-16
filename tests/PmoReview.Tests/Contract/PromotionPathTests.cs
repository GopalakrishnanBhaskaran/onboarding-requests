using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PmoReview.Adapters;
using PmoReview.Adapters.Dispatch;
using PmoReview.Adapters.Review;
using PmoReview.Tests.Support;
using Xunit;

namespace PmoReview.Tests.Contract;

/// <summary>
/// Phase 7 exit: flipping provider flags (from config/*.json) proves each paid path compiles and
/// passes the same contract with mocks. No network, no cost.
/// </summary>
public class PromotionPathTests
{
    private static Func<string, string?> Settings(string file, Dictionary<string, string>? overrides = null)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(dir!.FullName, "PmoReview.slnx"))) dir = dir.Parent;
        var values = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(dir.FullName, "config", file)))!;
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
        Assert.Equal(30, settings.ProcessingBudgetSeconds);

        var app = TestApp.With(settings: settings);
        var id = await app.CreateRequest();
        var a = await app.Analyze(id);
        Assert.Equal("Completed", a.Json.GetProperty("processingStatus").GetString());
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
        Assert.Equal("AzureOpenAI", ReviewModelFactory.Create(settings).Info.Provider); // real factory path compiles and builds

        var handler = new ScriptedHandler(() => ScriptedHandler.Final("""
            {"summary":"Complete.","findings":[],"mappingAssessment":{"status":"Matched","mappingId":"A-MAP-ENG-DEV","mappingVersion":1,"explanation":"ok"},
             "clarificationQuestions":[],"limitations":[]}
            """));
        var model = ScriptedHandler.Model(handler);
        var queue = new ListQueue();
        var app = TestApp.With(model, settings, queue);

        var id = await app.CreateRequest();
        var a = await app.Analyze(id);
        Assert.Equal("Queued", a.Json.GetProperty("processingStatus").GetString()); // returns immediately in the paid design

        var (processor, _) = PilotApp.BuildProcessing(app.Stores, settings, app.Clock, NullLoggerFactory.Instance, model);
        Assert.True(await new QueueMessageHandler(processor).HandleAsync(Assert.Single(queue.Messages)));

        var run = (await app.Run(a.Json.GetProperty("runId").GetString()!)).Value;
        Assert.Equal("Completed", run.GetProperty("processingStatus").GetString());
        Assert.Equal("AzureOpenAI", run.GetProperty("reviewerProvider").GetString());
        Assert.Equal("aoai-review-v1", run.GetProperty("promptVersion").GetString());
        Assert.Empty(PmoReview.Core.Review.ResultSchema.Validate(run.GetProperty("result").GetRawText()));
    }

    private sealed class ListQueue : IQueueClient
    {
        public List<string> Messages { get; } = [];

        public Task SendAsync(string message, CancellationToken ct)
        {
            Messages.Add(message);
            return Task.CompletedTask;
        }
    }
}
