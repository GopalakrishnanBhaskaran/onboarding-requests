using Microsoft.Extensions.Logging;
using PmoReview.Adapters.Dispatch;
using PmoReview.Adapters.Review;
using PmoReview.Adapters.Uploads;
using PmoReview.Core.Common;
using PmoReview.Core.Directory;
using PmoReview.Core.Http;
using PmoReview.Core.Offboarding;
using PmoReview.Core.Processing;
using PmoReview.Core.Review;
using PmoReview.Core.Security;
using PmoReview.Core.Services;
using PmoReview.Core.Sources;
using PmoReview.Core.Storage;
using PmoReview.Core.Uploads;
using PmoReview.Core.Workflow;

namespace PmoReview.Adapters;

/// <summary>Settings read from environment / app settings. Defaults are the zero-cost pilot.</summary>
public sealed record PilotSettings
{
    /// <summary>APP_ENVIRONMENT values. Only non-production environments may redirect mail to a test address.</summary>
    public static readonly IReadOnlyList<string> AppEnvironments = ["pilot", "dev", "test", "prod"];

    /// <summary>NOTIFY_SENDER values. Anything else refuses to start (re-check 6: a typo used to switch mail off silently).</summary>
    public static readonly IReadOnlyList<string> NotifySenders = ["Outbox", "AcsEmail", "GraphMail"];

    public bool AllowPaidServices { get; init; }
    /// <summary>Outbox (pilot: nothing is mailed) | AcsEmail | GraphMail. The worker builds its sender from this.</summary>
    public string NotifySender { get; init; } = "Outbox";
    public IReadOnlyCollection<string> AllowedIdentityProviders { get; init; } = ["aad"];
    /// <summary>RuleOnly (pilot) | AzureOpenAI (paid).</summary>
    public string ReviewModelProvider { get; init; } = "RuleOnly";
    /// <summary>Inline (pilot) | Queue (paid).</summary>
    public string Dispatcher { get; init; } = "Inline";
    /// <summary>Kept below the 45 s SWA gateway limit (VERIFIED_FACTS F3).</summary>
    public int ProcessingBudgetSeconds { get; init; } = 30;
    /// <summary>Running longer than this is marked Failed(Timeout) by the Operator or the worker.</summary>
    public int StuckRunMinutes { get; init; } = 5;
    /// <summary>Bundle submit: how long the agent's first steps may run inside the one HTTP call (null = 15 s); tests set zero.</summary>
    public TimeSpan? BundleInlineBudget { get; init; }
    public Uri? AzureOpenAIEndpoint { get; init; }
    public string? AzureOpenAIDeployment { get; init; }
    public string? AzureOpenAIApiVersion { get; init; }
    /// <summary>AI analysis in the daily summary and developer reports (worker; needs AzureOpenAI).</summary>
    public bool AiReports { get; init; }
    public WorkflowSettings Workflow { get; init; } = new();

    public static PilotSettings FromEnvironment(Func<string, string?> get)
    {
        bool On(string name) => string.Equals(get(name), "true", StringComparison.OrdinalIgnoreCase);
        // double.TryParse accepts "NaN", which TimeSpan refuses with an unclear error at start-up: a non-finite value is the default.
        TimeSpan Hours(string name, double fallback) => TimeSpan.FromHours(Math.Clamp(
            double.TryParse(get(name), System.Globalization.CultureInfo.InvariantCulture, out var h) && double.IsFinite(h) ? h : fallback, 0.1, 24 * 30));
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
        // Re-check 6: settings that decide what the agent may read or change are refused when they cannot be read, instead of
        // being dropped. A tenant that is not a GUID made the agent leave every real project alone without saying why; a
        // subscription list with a typo became empty, which means "every subscription the worker can list" (wider).
        var tenant = get("AGENT_TENANT_ID")?.Trim();
        if (!string.IsNullOrEmpty(tenant) && !Guid.TryParse(tenant, out _))
            throw new InvalidOperationException("AGENT_TENANT_ID must be the tenant id (a GUID), not a domain name.");
        var offboardSubscriptions = List("OFFBOARD_SUBSCRIPTIONS");
        if (offboardSubscriptions.FirstOrDefault(x => !Guid.TryParse(x, out _)) is { } badSubscription)
            throw new InvalidOperationException($"OFFBOARD_SUBSCRIPTIONS takes subscription ids (GUIDs), comma-separated; '{badSubscription}' is not one.");
        var sender = get("NOTIFY_SENDER")?.Trim();
        if (string.IsNullOrEmpty(sender)) sender = "Outbox";
        if (!NotifySenders.Contains(sender, StringComparer.Ordinal))
            throw new InvalidOperationException($"NOTIFY_SENDER must be one of {string.Join(", ", NotifySenders)} (exact spelling).");
        static string? Mailbox(string? value, string name)
        {
            var v = value?.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(v)) return null;
            return System.Text.RegularExpressions.Regex.IsMatch(v, "^[^@\\s]+@[^@\\s]+\\.[^@\\s]+$") ? v
                : throw new InvalidOperationException($"{name} must be one mail address.");
        }
        return new PilotSettings
        {
            AllowPaidServices = On("ALLOW_PAID_SERVICES"),
            NotifySender = sender,
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
                MailEnabled = sender is "GraphMail" or "AcsEmail",
                MailRedirectTo = redirect,
                // ACS Azure-managed domain by default (5 a minute, 10 an hour, F70); a custom domain: e.g. 30 / 100.
                MailLimits = new MailLimits(
                    Math.Clamp(int.TryParse(get("MAIL_MAX_PER_MINUTE"), out var mpm) ? mpm : MailLimits.AzureManagedDomain.PerMinute, 1, 100_000),
                    Math.Clamp(int.TryParse(get("MAIL_MAX_PER_HOUR"), out var mph) ? mph : MailLimits.AzureManagedDomain.PerHour, 1, 1_000_000)),
                // v1.1 (30 Oct 2026): every switch off by default.
                AgentOffboarding = On("AGENT_OFFBOARDING"),
                // v1.2 (2 Nov 2026): Databricks set-up of a request's group, off by default (deploy_free.sh databricks, approval-gated).
                AgentDatabricks = On("AGENT_DATABRICKS"),
                DatabricksAccountId = Guid.TryParse(get("DATABRICKS_ACCOUNT_ID"), out var dbxAcc) ? dbxAcc.ToString("D") : null,
                DatabricksWorkspaces = List("DATABRICKS_WORKSPACES").Select(PmoReview.Core.Databricks.DatabricksWorkspace.Parse).OfType<PmoReview.Core.Databricks.DatabricksWorkspace>().ToList(),
                DatabricksGrantCatalogs = List("DATABRICKS_GRANT_CATALOGS").Where(c => System.Text.RegularExpressions.Regex.IsMatch(c, "^[A-Za-z0-9_]{1,255}$")).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                OffboardDeleteUser = On("OFFBOARD_DELETE_USER"),
                OffboardSubscriptions = offboardSubscriptions.Select(x => Guid.Parse(x).ToString("D")).Distinct().ToList(),
                TapMailSender = Mailbox(get("TAP_MAIL_SENDER"), "TAP_MAIL_SENDER"),
                MaxUploadBytes = Math.Clamp(int.TryParse(get("MAX_UPLOAD_BYTES"), out var mub) ? mub : 5 * 1024 * 1024, 1024, 25 * 1024 * 1024),
                ExternalUploadApiEnabled = On("EXTERNAL_UPLOAD_API_ENABLED"),
                ExternalUploadAudience = Guid.TryParse(get("EXTERNAL_UPLOAD_AUDIENCE"), out var aud) ? aud.ToString("D") : null,
            },
        };
    }
}

/// <summary>
/// The AI agent worker's start-up rules, in one place the tests call (re-check 6: they lived only in the worker's top-level
/// <c>Program.cs</c>, checked by reading its text). <see cref="Check"/> refuses to start on a setting the worker must not run
/// with; <see cref="Capabilities"/> says which writers and mail the worker builds, so the tests' worker is wired by the same rule.
/// </summary>
public static class WorkerStartup
{
    /// <summary>Phase 2 access-package switches: refused when true (v2.1 provisioning is AGENT_PROVISIONING / AGENT_ROLE_ASSIGNMENT).</summary>
    public static readonly IReadOnlyList<string> RetiredSettings = ["ENTRA_AUTO_ASSIGN", "ENTRA_PROVISION_JOINERS", "ENTRA_AUTO_CREATE_PACKAGES"];

    /// <summary>The checks before anything is built. Returns the settings and the Cosmos endpoint (managed identity; no keys).</summary>
    public static (PilotSettings Settings, Uri CosmosEndpoint) Check(Func<string, string?> get)
    {
        bool On(string name) => string.Equals(get(name), "true", StringComparison.OrdinalIgnoreCase);
        if (!On("ALLOW_PAID_SERVICES"))
            throw new InvalidOperationException("The workflow worker is a paid component. Set ALLOW_PAID_SERVICES=true only in the approved environment.");
        if (!Uri.TryCreate(get("COSMOS_ENDPOINT"), UriKind.Absolute, out var cosmos) || cosmos.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("COSMOS_ENDPOINT is required (https; managed identity; no keys).");
        foreach (var retired in RetiredSettings)
            if (On(retired))
                throw new InvalidOperationException($"{retired}=true is retired (Phase 2 access packages). v2.1 provisioning is AGENT_PROVISIONING / AGENT_ROLE_ASSIGNMENT, after both approvals.");
        return (PilotSettings.FromEnvironment(get), cosmos);
    }

    /// <summary>What the worker holds for these settings. Every write capability exists only with its switch.</summary>
    public sealed record WorkerCapabilities(bool DirectoryWriter, bool RoleWriter, bool TapMail, bool OffboardingWriters, bool RoleReader);

    public static WorkerCapabilities Capabilities(PilotSettings s, Func<string, string?> get) => new(
        DirectoryWriter: s.Workflow.AgentProvisioning,
        RoleWriter: s.Workflow.AgentRoleAssignment,
        // F2: the sign-in details go only through Graph sendMail from TAP_MAIL_SENDER, and only for accounts the agent creates.
        TapMail: s.Workflow.TapMailByAgent,
        OffboardingWriters: s.Workflow.AgentOffboarding,
        RoleReader: get("ROLE_READER") == "Arm");
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
    ContactInventoryService Contacts,
    ResourceService Resources,
    RequestBundleService Bundles,
    UploadService Uploads,
    OffboardingService Offboarding);

/// <summary>Composition root shared by the Functions hosts and the tests, so tests exercise the real wiring.</summary>
public static class PilotApp
{
    /// <param name="model">Test seam; production chooses from settings (paid providers are refused unless allowed).</param>
    /// <param name="queue">Paid environment only.</param>
    /// <param name="directory">Read-only Entra reader. None in the SWA-managed API (no managed identity): the worker has it.</param>
    /// <remarks>The API never gets a directory or role writer: only the worker provisions (v2.1).</remarks>
    /// <param name="signingKeys">F4 external API: the tenants' token signing keys (test seam; default: Entra's public OpenID metadata).</param>
    public static ApiHost Build(DataStores stores, PilotSettings settings, IClock clock, ILoggerFactory loggers,
        IReviewModel? model = null, IQueueClient? queue = null, IDirectoryReader? directory = null, IRoleAssignmentReader? roles = null,
        ISigningKeys? signingKeys = null)
    {
        var parts = BuildWorkflow(stores, settings, clock, loggers, model, queue, directory, roles);
        var host = new ApiHost(new AccessService(stores), settings.AllowedIdentityProviders, loggers.CreateLogger<ApiHost>(), new ApiFailureLog(stores, parts.Issues));
        host.MapCore(stores, parts.Service, parts.MappingSheet, parts.Contacts);
        host.MapRuns(parts.Runs);
        host.MapV11(parts.Service, parts.Resources, parts.Bundles);
        // F4: the Data uploads page, and the external upload API (404 unless EXTERNAL_UPLOAD_API_ENABLED=true).
        var tokens = settings.Workflow.ExternalUploadApiEnabled
            ? new UploadTokenValidator(signingKeys ?? new OpenIdSigningKeys(new HttpClient { Timeout = TimeSpan.FromSeconds(10) }, clock), settings.Workflow.ExternalUploadAudience, clock,
                async (tid, ct) => (await stores.Projects.ListAllAsync(ct)).Any(p => string.Equals(p.Value.TenantId?.Trim(), tid, StringComparison.OrdinalIgnoreCase)))
            : null;
        host.MapUploads(parts.Uploads, stores, tokens, settings.Workflow);
        host.MapOffboarding(parts.Offboarding); // F5: plans and confirmations only; the worker reads and writes Entra / Azure

        return host;
    }

    public static WorkflowParts BuildWorkflow(DataStores stores, PilotSettings settings, IClock clock, ILoggerFactory loggers,
        IReviewModel? model = null, IQueueClient? queue = null, IDirectoryReader? directory = null, IRoleAssignmentReader? roles = null,
        IAiAssistant? assistant = null, IDirectoryWriter? writer = null, IRoleAssignmentWriter? roleWriter = null, IDirectMail? directMail = null,
        PmoReview.Core.Databricks.IDatabricksAccess? databricksAccess = null)
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
        // v1.2: Databricks set-up and tests exist only with AGENT_DATABRICKS (the worker passes the access then).
        var databricks = databricksAccess is null ? null : new PmoReview.Core.Databricks.DatabricksConfig(databricksAccess,
            settings.Workflow.DatabricksWorkspaces, settings.Workflow.DatabricksGrantCatalogs, PmoReview.Core.Databricks.DatabricksPolicy.Default);
        var verifier = new Verifier(directory, roles, settings.Workflow, databricks);
        var agent = new WorkflowAgent(stores, sources, runs, notifier, issues, verifier, settings.Workflow, policy, clock,
            loggers.CreateLogger<WorkflowAgent>(), canCrossCheck: engine.CanCrossCheck, directory, assistant, writer, roleWriter, directMail, databricks);
        var service = new WorkflowService(stores, new RequestService(stores, clock), agent, sources, notifier, issues, settings.Workflow, clock,
            loggers.CreateLogger<WorkflowService>());
        var resources = new ResourceService(stores, clock, settings.Workflow.AgentTenantId, settings.Workflow.AllowUnverifiedEntra);
        return new WorkflowParts(stores, runs, engine, processor, agent, service, new MappingSheetService(stores, clock), notifier, issues,
            new ContactInventoryService(stores, notifier, clock), resources, new RequestBundleService(resources, service, settings.BundleInlineBudget), new UploadService(stores, clock, settings.Workflow),
            new OffboardingService(stores, clock, settings.Workflow));
    }
}
