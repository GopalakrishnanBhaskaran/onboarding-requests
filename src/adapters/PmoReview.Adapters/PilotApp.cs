using Microsoft.Extensions.Logging;
using PmoReview.Adapters.Dispatch;
using PmoReview.Adapters.Review;
using PmoReview.Core.Common;
using PmoReview.Core.Directory;
using PmoReview.Core.Http;
using PmoReview.Core.Processing;
using PmoReview.Core.Review;
using PmoReview.Core.Security;
using PmoReview.Core.Services;
using PmoReview.Core.Sources;
using PmoReview.Core.Storage;
using PmoReview.Core.Workflow;

namespace PmoReview.Adapters;

/// <summary>Settings read from environment / app settings. Defaults are the zero-cost pilot.</summary>
public sealed record PilotSettings
{
    /// <summary>APP_ENVIRONMENT values. Only non-production environments may redirect mail to a test address.</summary>
    public static readonly IReadOnlyList<string> AppEnvironments = ["pilot", "dev", "test", "prod"];

    public bool AllowPaidServices { get; init; }
    public IReadOnlyCollection<string> AllowedIdentityProviders { get; init; } = ["aad"];
    /// <summary>RuleOnly (pilot) | AzureOpenAI (paid).</summary>
    public string ReviewModelProvider { get; init; } = "RuleOnly";
    /// <summary>Inline (pilot) | Queue (paid).</summary>
    public string Dispatcher { get; init; } = "Inline";
    /// <summary>Kept below the 45 s SWA gateway limit (VERIFIED_FACTS F3).</summary>
    public int ProcessingBudgetSeconds { get; init; } = 30;
    /// <summary>Running longer than this is marked Failed(Timeout) by the Operator or the worker.</summary>
    public int StuckRunMinutes { get; init; } = 5;
    public Uri? AzureOpenAIEndpoint { get; init; }
    public string? AzureOpenAIDeployment { get; init; }
    public string? AzureOpenAIApiVersion { get; init; }
    /// <summary>AI analysis in the daily summary and developer reports (worker; needs AzureOpenAI).</summary>
    public bool AiReports { get; init; }
    public WorkflowSettings Workflow { get; init; } = new();

    public static PilotSettings FromEnvironment(Func<string, string?> get)
    {
        bool On(string name) => string.Equals(get(name), "true", StringComparison.OrdinalIgnoreCase);
        TimeSpan Hours(string name, double fallback) => TimeSpan.FromHours(Math.Clamp(double.TryParse(get(name), System.Globalization.CultureInfo.InvariantCulture, out var h) ? h : fallback, 0.1, 24 * 30));
        IReadOnlyList<string> List(string name) =>
            (get(name) ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var unverified = On("ALLOW_UNVERIFIED_ENTRA");
        if (unverified && !On("PILOT_LOCAL_DEV"))
            throw new InvalidOperationException("ALLOW_UNVERIFIED_ENTRA=true is only allowed with PILOT_LOCAL_DEV=true (local development).");
        var environment = (get("APP_ENVIRONMENT") ?? "pilot").Trim().ToLowerInvariant();
        if (!AppEnvironments.Contains(environment))
            throw new InvalidOperationException($"APP_ENVIRONMENT must be one of {string.Join(", ", AppEnvironments)}.");
        var redirect = get("NOTIFY_REDIRECT_TO")?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(redirect)) redirect = null;
        if (redirect is not null && environment == "prod")
            throw new InvalidOperationException("NOTIFY_REDIRECT_TO is for test environments only; production mails the real recipients.");
        if (redirect is not null && !System.Text.RegularExpressions.Regex.IsMatch(redirect, "^[^@\\s]+@[^@\\s]+\\.[^@\\s]+$"))
            throw new InvalidOperationException("NOTIFY_REDIRECT_TO must be one mail address.");
        var tenant = get("AGENT_TENANT_ID")?.Trim();
        return new PilotSettings
        {
            AllowPaidServices = On("ALLOW_PAID_SERVICES"),
            AllowedIdentityProviders = (get("ALLOWED_IDENTITY_PROVIDERS") ?? "aad")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            ReviewModelProvider = get("REVIEW_MODEL_PROVIDER") ?? "RuleOnly",
            Dispatcher = get("REVIEW_DISPATCHER") ?? "Inline",
            ProcessingBudgetSeconds = Math.Clamp(int.TryParse(get("PROCESSING_BUDGET_SECONDS"), out var b) ? b : 30, 5, 40),
            StuckRunMinutes = Math.Clamp(int.TryParse(get("STUCK_RUN_MINUTES"), out var m) ? m : 5, 1, 120),
            AzureOpenAIEndpoint = Uri.TryCreate(get("AZURE_OPENAI_ENDPOINT"), UriKind.Absolute, out var u) ? u : null,
            AzureOpenAIDeployment = get("AZURE_OPENAI_DEPLOYMENT"),
            AzureOpenAIApiVersion = get("AZURE_OPENAI_API_VERSION"),
            AiReports = On("AI_REPORTS"),
            Workflow = new WorkflowSettings
            {
                OwnerSla = Hours("OWNER_SLA_HOURS", 24),
                SecuritySla = Hours("SECURITY_SLA_HOURS", 24),
                IdTeamSla = Hours("ID_TEAM_SLA_HOURS", 24),
                RoleTeamSla = Hours("ROLE_TEAM_SLA_HOURS", 24),
                VerifySla = Hours("VERIFY_SLA_HOURS", 2),
                TapMaxLifetimeMinutes = Math.Clamp(int.TryParse(get("TAP_MAX_LIFETIME_MINUTES"), out var t) ? t : 60, 10, 480),
                AllowUnverifiedEntra = unverified,
                AppUrl = Uri.TryCreate(get("APP_URL"), UriKind.Absolute, out var app) && app.Scheme == Uri.UriSchemeHttps ? app.AbsoluteUri : null,
                // v2.1 agent provisioning: needs the approved write permissions (deploy_free.sh provisioning).
                AgentProvisioning = On("AGENT_PROVISIONING"),
                AgentRoleAssignment = On("AGENT_ROLE_ASSIGNMENT"),
                // Agent TAP (approval-gated: deploy_free.sh tap): the agent mails a new account's sign-in details once.
                AgentTap = On("AGENT_TAP"),
                AgentPrivileged = On("AGENT_PRIVILEGED"),
                AgentTapLifetimeMinutes = Math.Clamp(int.TryParse(get("AGENT_TAP_LIFETIME_MINUTES"), out var al) ? al : 480, 60, 43200),
                RoleAssignAllowedRoleIds = List("ROLE_ASSIGN_ALLOWED_ROLES").Where(r => Guid.TryParse(r, out _)).Select(r => r.ToLowerInvariant()).ToList(),
                RoleAssignScopes = List("ROLE_ASSIGN_SCOPES").Where(s => s.StartsWith("/subscriptions/", StringComparison.OrdinalIgnoreCase)).ToList(),
                AgentTenantId = Guid.TryParse(tenant, out var tid) ? tid.ToString("D") : null,
                AppEnvironment = environment,
                MailEnabled = get("NOTIFY_SENDER") is "GraphMail" or "AcsEmail",
                MailRedirectTo = redirect,
                // ACS Azure-managed domain by default (5 a minute, 10 an hour, F70); a custom domain: e.g. 30 / 100.
                MailLimits = new MailLimits(
                    Math.Clamp(int.TryParse(get("MAIL_MAX_PER_MINUTE"), out var mpm) ? mpm : MailLimits.AzureManagedDomain.PerMinute, 1, 100_000),
                    Math.Clamp(int.TryParse(get("MAIL_MAX_PER_HOUR"), out var mph) ? mph : MailLimits.AzureManagedDomain.PerHour, 1, 1_000_000)),
            },
        };
    }
}

/// <summary>Everything the API, the worker and the tests share. Built once by <see cref="PilotApp.BuildWorkflow"/>.</summary>
public sealed record WorkflowParts(
    DataStores Stores,
    RunService Runs,
    ReviewEngine Engine,
    ReviewProcessor Processor,
    WorkflowAgent Agent,
    WorkflowService Service,
    MappingSheetService MappingSheet,
    Notifier Notifier,
    IssueLog Issues,
    ContactInventoryService Contacts);

/// <summary>Composition root shared by the Functions hosts and the tests, so tests exercise the real wiring.</summary>
public static class PilotApp
{
    /// <param name="model">Test seam; production chooses from settings (paid providers are refused unless allowed).</param>
    /// <param name="queue">Paid environment only.</param>
    /// <param name="directory">Read-only Entra reader. None in the SWA-managed API (no managed identity): the worker has it.</param>
    /// <remarks>The API never gets a directory or role writer: only the worker provisions (v2.1).</remarks>
    public static ApiHost Build(DataStores stores, PilotSettings settings, IClock clock, ILoggerFactory loggers,
        IReviewModel? model = null, IQueueClient? queue = null, IDirectoryReader? directory = null, IRoleAssignmentReader? roles = null)
    {
        var parts = BuildWorkflow(stores, settings, clock, loggers, model, queue, directory, roles);
        var host = new ApiHost(new AccessService(stores), settings.AllowedIdentityProviders, loggers.CreateLogger<ApiHost>());
        host.MapCore(stores, parts.Service, parts.MappingSheet, parts.Contacts);
        host.MapRuns(parts.Runs);
        return host;
    }

    public static WorkflowParts BuildWorkflow(DataStores stores, PilotSettings settings, IClock clock, ILoggerFactory loggers,
        IReviewModel? model = null, IQueueClient? queue = null, IDirectoryReader? directory = null, IRoleAssignmentReader? roles = null,
        IAiAssistant? assistant = null, IDirectoryWriter? writer = null, IRoleAssignmentWriter? roleWriter = null, IDirectMail? directMail = null)
    {
        var reviewer = model ?? ReviewModelFactory.Create(settings, logger: loggers.CreateLogger("AzureOpenAI"));
        var sources = new StoreSourcesOfTruth(stores);
        var policy = new ReviewPolicy();
        var budget = TimeSpan.FromSeconds(settings.ProcessingBudgetSeconds);
        // The model gets most of the budget; the rest covers claim, reads and commit.
        var modelBudget = budget > TimeSpan.FromSeconds(10) ? budget - TimeSpan.FromSeconds(5) : budget;
        var engine = new ReviewEngine(stores, sources, reviewer, new ReviewLimits { TimeBudget = modelBudget }, policy, directory,
            skipEntraLocalDev: settings.Workflow.AllowUnverifiedEntra, agentTenantId: settings.Workflow.AgentTenantId);
        var processor = new ReviewProcessor(stores, engine, clock, budget, loggers.CreateLogger<ReviewProcessor>());
        var dispatcher = DispatcherFactory.Create(settings, processor, queue);
        var runs = new RunService(stores, dispatcher, reviewer.Info, clock, TimeSpan.FromMinutes(settings.StuckRunMinutes));
        var issues = new IssueLog(stores, clock, loggers.CreateLogger<IssueLog>());
        var notifier = new Notifier(stores, clock, loggers.CreateLogger<Notifier>(), issues, settings.Workflow.MailEnabled);
        var verifier = new Verifier(directory, roles, settings.Workflow);
        var agent = new WorkflowAgent(stores, sources, runs, notifier, issues, verifier, settings.Workflow, policy, clock,
            loggers.CreateLogger<WorkflowAgent>(), canCrossCheck: engine.CanCrossCheck, directory, assistant, writer, roleWriter, directMail);
        var service = new WorkflowService(stores, new RequestService(stores, clock), agent, sources, notifier, issues, settings.Workflow, clock,
            loggers.CreateLogger<WorkflowService>());
        return new WorkflowParts(stores, runs, engine, processor, agent, service, new MappingSheetService(stores, clock), notifier, issues,
            new ContactInventoryService(stores, notifier, clock));
    }
}
