using Microsoft.Extensions.Logging;
using PmoReview.Adapters.Dispatch;
using PmoReview.Adapters.Review;
using PmoReview.Core.Common;
using PmoReview.Core.Handoff;
using PmoReview.Core.Processing;
using PmoReview.Core.Review;
using PmoReview.Core.Http;
using PmoReview.Core.Security;
using PmoReview.Core.Services;
using PmoReview.Core.Storage;

namespace PmoReview.Adapters;

/// <summary>Settings read from environment / SWA app settings. Defaults are the zero-cost pilot.</summary>
public sealed record PilotSettings
{
    public bool AllowPaidServices { get; init; }
    public IReadOnlyCollection<string> AllowedIdentityProviders { get; init; } = ["aad"];
    /// <summary>RuleOnly (pilot) | AzureOpenAI (paid).</summary>
    public string ReviewModelProvider { get; init; } = "RuleOnly";
    /// <summary>Inline (pilot) | Queue (paid).</summary>
    public string Dispatcher { get; init; } = "Inline";
    /// <summary>Kept below the 45 s SWA gateway limit (VERIFIED_FACTS F3). Q3 = 30 s.</summary>
    public int ProcessingBudgetSeconds { get; init; } = 30;
    /// <summary>Operator recovery: Running longer than this is marked Failed(Timeout).</summary>
    public int StuckRunMinutes { get; init; } = 5;
    public Uri? AzureOpenAIEndpoint { get; init; }
    public string? AzureOpenAIDeployment { get; init; }
    public string? AzureOpenAIApiVersion { get; init; }
    /// <summary>Phase 2: create Entra hand-off records on accepted reviews (needs access packages + the sync worker).</summary>
    public bool EntraHandoffEnabled { get; init; }
    public bool RequireSeparateReviewer { get; init; }
    /// <summary>ENTRA_AUTO_ASSIGN: a second Reviewer's acceptance is the access decision (forces separate reviewers).</summary>
    public bool EntraAutoAssign { get; init; }

    public static PilotSettings FromEnvironment(Func<string, string?> get) => new()
    {
        AllowPaidServices = string.Equals(get("ALLOW_PAID_SERVICES"), "true", StringComparison.OrdinalIgnoreCase),
        AllowedIdentityProviders = (get("ALLOWED_IDENTITY_PROVIDERS") ?? "aad")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        ReviewModelProvider = get("REVIEW_MODEL_PROVIDER") ?? "RuleOnly",
        Dispatcher = get("REVIEW_DISPATCHER") ?? "Inline",
        ProcessingBudgetSeconds = Math.Clamp(int.TryParse(get("PROCESSING_BUDGET_SECONDS"), out var b) ? b : 30, 5, 40),
        StuckRunMinutes = Math.Clamp(int.TryParse(get("STUCK_RUN_MINUTES"), out var m) ? m : 5, 1, 120),
        AzureOpenAIEndpoint = Uri.TryCreate(get("AZURE_OPENAI_ENDPOINT"), UriKind.Absolute, out var u) ? u : null,
        AzureOpenAIDeployment = get("AZURE_OPENAI_DEPLOYMENT"),
        AzureOpenAIApiVersion = get("AZURE_OPENAI_API_VERSION"),
        EntraHandoffEnabled = string.Equals(get("ENTRA_HANDOFF_ENABLED"), "true", StringComparison.OrdinalIgnoreCase),
        RequireSeparateReviewer = string.Equals(get("REQUIRE_SEPARATE_REVIEWER"), "true", StringComparison.OrdinalIgnoreCase),
        EntraAutoAssign = string.Equals(get("ENTRA_AUTO_ASSIGN"), "true", StringComparison.OrdinalIgnoreCase),
    };
}

/// <summary>Composition root shared by the Functions host and the tests, so tests exercise the real wiring.</summary>
public static class PilotApp
{
    /// <param name="model">Test seam; production chooses from settings (paid providers are refused unless allowed).</param>
    /// <param name="queue">Paid environment only.</param>
    public static ApiHost Build(DataStores stores, PilotSettings settings, IClock clock, ILoggerFactory loggers,
        IReviewModel? model = null, IQueueClient? queue = null)
    {
        var reviewer = model ?? ReviewModelFactory.Create(settings);
        var (processor, _) = BuildProcessing(stores, settings, clock, loggers, reviewer);
        var dispatcher = DispatcherFactory.Create(settings, processor, queue);
        var handoffs = new HandoffService(stores, new HandoffSettings { Enabled = settings.EntraHandoffEnabled, RequireSeparateReviewer = settings.RequireSeparateReviewer, AutoAssign = settings.EntraAutoAssign }, clock);
        var runs = new RunService(stores, dispatcher, reviewer.Info, clock, TimeSpan.FromMinutes(settings.StuckRunMinutes), handoffs);

        var host = new ApiHost(new AccessService(stores), settings.AllowedIdentityProviders, loggers.CreateLogger<ApiHost>());
        host.MapCore(stores, new RequestService(stores, clock), new ReferenceDataService(stores));
        host.MapRuns(runs);
        host.MapHandoffs(handoffs);
        return host;
    }

    /// <summary>Engine + processor. Shared with the queue-triggered path in the paid environment.</summary>
    public static (ReviewProcessor Processor, ReviewEngine Engine) BuildProcessing(
        DataStores stores, PilotSettings settings, IClock clock, ILoggerFactory loggers, IReviewModel reviewer)
    {
        var budget = TimeSpan.FromSeconds(settings.ProcessingBudgetSeconds);
        // The model gets most of the budget; the rest covers claim, reads and commit.
        var modelBudget = budget > TimeSpan.FromSeconds(10) ? budget - TimeSpan.FromSeconds(5) : budget;
        var engine = new ReviewEngine(stores, reviewer, new ReviewLimits { TimeBudget = modelBudget });
        return (new ReviewProcessor(stores, engine, clock, budget, loggers.CreateLogger<ReviewProcessor>()), engine);
    }
}
