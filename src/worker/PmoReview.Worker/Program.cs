using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PmoReview.Adapters;
using PmoReview.Adapters.Databricks;
using PmoReview.Adapters.Entra;
using PmoReview.Adapters.Outbound;
using PmoReview.Adapters.Rbac;
using PmoReview.Adapters.Review;
using PmoReview.Adapters.Storage;
using PmoReview.Core.Common;
using PmoReview.Core.Directory;
using PmoReview.Core.Offboarding;
using PmoReview.Core.Storage;
using PmoReview.Core.Workflow;

// The AI agent's background worker (paid component). Refuses to start unless paid services are explicitly allowed
// (CLAUDE.md rule 11). Managed identity only. The agent never approves. Entra and Azure RBAC are read only unless the
// approved v2.1 provisioning is switched on (AGENT_PROVISIONING / AGENT_ROLE_ASSIGNMENT): then, only after the Owner and
// Security approvals, it creates the user, group and membership and assigns allow-listed Azure roles. With the approved
// AGENT_TAP (and a mail sender) it also mails a new account's one-time TAP and initial password to the joiner, once. With the
// approved AGENT_DATABRICKS it sets up a Databricks row's group (account, workspace, entitlements, Unity Catalog) and tests it.
// The start-up checks (ALLOW_PAID_SERVICES, COSMOS_ENDPOINT, the retired ENTRA_* switches, every setting) and which writers
// and mail exist live in WorkerStartup, so the tests run the same rules (re-check 6).
string? Env(string name) => Environment.GetEnvironmentVariable(name);
var (settings, cosmosEndpoint) = WorkerStartup.Check(Env);
var can = WorkerStartup.Capabilities(settings, Env);

var credential = new Azure.Identity.DefaultAzureCredential();
HttpClient Http() => new() { Timeout = TimeSpan.FromSeconds(30) };

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices(services =>
    {
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton(_ => CosmosDataStores.Create(
            CosmosDataStores.CreateClientWithIdentity(cosmosEndpoint, credential), Env("COSMOS_DATABASE") ?? "pmoreview"));
        services.AddSingleton<IDirectoryReader>(_ => new GraphDirectoryReader(Http(), credential));
        // NOTIFY_SENDER is checked at start-up (a misspelt name refuses to start, re-check 6). Incomplete mail settings never
        // stop the agent (they used to throw here, on every run): each mail fails with the reason instead, on the request page
        // and the Issues list.
        services.AddSingleton<INotificationSender>(_ => settings.NotifySender switch
        {
            "GraphMail" => string.IsNullOrWhiteSpace(Env("NOTIFY_FROM"))
                ? new MisconfiguredSender("NOTIFY_SENDER=GraphMail needs NOTIFY_FROM (the shared sender mailbox)")
                : new GraphMailSender(Http(), credential, Env("NOTIFY_FROM")!),
            // Azure Communication Services Email with the worker's managed identity (deploy_free.sh email, approval-gated).
            "AcsEmail" => !Uri.TryCreate(Env("ACS_ENDPOINT"), UriKind.Absolute, out var acs)
                ? new MisconfiguredSender("NOTIFY_SENDER=AcsEmail needs ACS_ENDPOINT (https://<name>.communication.azure.com)")
                : string.IsNullOrWhiteSpace(Env("NOTIFY_FROM"))
                    ? new MisconfiguredSender("NOTIFY_SENDER=AcsEmail needs NOTIFY_FROM (DoNotReply@<managed domain>)")
                    : new AcsEmailSender(acs, credential, Env("NOTIFY_FROM")!),
            _ => new OutboxOnlySender(),
        });
        services.AddSingleton(sp =>
        {
            var loggers = sp.GetRequiredService<ILoggerFactory>();
            IRoleAssignmentReader? roles = can.RoleReader ? new ArmRoleAssignmentReader(Http(), credential) : null;
            IAiAssistant? assistant = settings.AiReports && settings.ReviewModelProvider == AzureOpenAIReviewModel.ProviderName
                ? new AzureOpenAIAssistant(ReviewModelFactory.Chat(settings, logger: loggers.CreateLogger("AzureOpenAI")))
                : null;
            IDirectoryWriter? writer = can.DirectoryWriter ? new GraphDirectoryWriter(Http(), credential) : null;
            IRoleAssignmentWriter? roleWriter = can.RoleWriter
                ? new ArmRoleAssignmentWriter(Http(), credential, settings.Workflow.RoleAssignAllowedRoleIds)
                : null;
            // Agent TAP (F2): the sign-in details go only through Graph sendMail from the one TAP_MAIL_SENDER mailbox
            // (Application Mail.Send scoped to it by Exchange RBAC for Applications, F81), to one verified address, never
            // redirected and never through the stored outbox. Without the setting the ID team issues every pass.
            // TAP_MAIL_VIA=acs (7 Nov 2026): the same one-recipient direct mail through the app's ACS sender instead (no new
            // permission; ACS processes the content in real time, F138). Misconfigured ACS: the send fails and the ID team hands the pass over.
            IDirectMail? directMail = !can.TapMail ? null
                : settings.Workflow.TapMailViaAcs
                    ? new DirectMail(Uri.TryCreate(Env("ACS_ENDPOINT"), UriKind.Absolute, out var tapAcs) && !string.IsNullOrWhiteSpace(Env("NOTIFY_FROM"))
                        ? new AcsEmailSender(tapAcs, credential, Env("NOTIFY_FROM")!)
                        : new MisconfiguredSender("TAP_MAIL_VIA=acs needs ACS_ENDPOINT and NOTIFY_FROM"))
                    : new DirectMail(new GraphMailSender(Http(), credential, settings.Workflow.TapMailSender!));
            // v1.2 (AGENT_DATABRICKS, approval-gated: deploy_free.sh databricks): the Databricks set-up of a request's group, after
            // both approvals, with the worker's managed identity as a Databricks service principal. Needs the role assignment step on.
            var databricks = settings.Workflow.AgentDatabricks && roleWriter is not null && settings.Workflow.DatabricksAccountId is { } dbxAccount
                ? new DatabricksAccessClient(Http(), credential, dbxAccount)
                : null;
            return PilotApp.BuildWorkflow(sp.GetRequiredService<DataStores>(), settings, sp.GetRequiredService<IClock>(), loggers,
                directory: sp.GetRequiredService<IDirectoryReader>(), roles: roles, assistant: assistant, writer: writer, roleWriter: roleWriter, directMail: directMail,
                databricksAccess: databricks);
        });
        services.AddSingleton(sp =>
        {
            var parts = sp.GetRequiredService<WorkflowParts>();
            var loggers = sp.GetRequiredService<ILoggerFactory>();
            var clock = sp.GetRequiredService<IClock>();
            var sender = sp.GetRequiredService<INotificationSender>();
            IWorkItemTracker tracker = Uri.TryCreate(Env("ADO_ORG_URL"), UriKind.Absolute, out var org) && !string.IsNullOrWhiteSpace(Env("ADO_PROJECT"))
                ? new AzureDevOpsWorkItemTracker(Http(), credential, new AzureDevOpsOptions
                {
                    Organization = org, Project = Env("ADO_PROJECT")!,
                    WorkItemType = Env("ADO_WORK_ITEM_TYPE") ?? "Task", ClosedState = Env("ADO_CLOSED_STATE") ?? "Done",
                })
                : new DisabledWorkItemTracker();
            var assistant = settings.AiReports && settings.ReviewModelProvider == AzureOpenAIReviewModel.ProviderName
                ? new AzureOpenAIAssistant(ReviewModelFactory.Chat(settings, logger: loggers.CreateLogger("AzureOpenAI")))
                : null;
            // v1.1 F5: offboarding plans are read-only (Graph reader, ARM reader); the writes exist only with AGENT_OFFBOARDING.
            var reader = sp.GetRequiredService<IDirectoryReader>();
            var offboarding = new OffboardingJob(parts.Stores, reader, (IOffboardingReader)reader, new ArmRoleAssignmentReader(Http(), credential),
                can.OffboardingWriters ? new GraphOffboardingWriter(Http(), credential, settings.Workflow.OffboardDeleteUser) : null,
                can.OffboardingWriters ? new ArmRoleAssignmentRemover(Http(), credential) : null,
                parts.Notifier, parts.Issues, clock, settings.Workflow, loggers.CreateLogger<OffboardingJob>());
            return new WorkflowWorker(parts.Agent,
                new NotificationDispatcher(parts.Stores, sender, parts.Issues, clock, loggers.CreateLogger<NotificationDispatcher>(),
                    redirectTo: settings.Workflow.MailRedirectTo, environment: settings.Workflow.AppEnvironment,
                    // Within the sender's limits (F70). The sign-in details no longer use this queue (F2: Graph, TAP_MAIL_SENDER).
                    limits: settings.Workflow.MailLimits),
                new AdoSync(parts.Stores, tracker, parts.Issues, clock, loggers.CreateLogger<AdoSync>()),
                new DailySummaryJob(parts.Stores, parts.Notifier, clock, int.TryParse(Env("DAILY_SUMMARY_HOUR_UTC"), out var hour) ? hour : 6, assistant),
                // v1.1 (F1): mapping group names resolved in Entra (read-only), unresolved groups escalated.
                new GroupResolutionJob(parts.Stores, sp.GetRequiredService<IDirectoryReader>(), clock, loggers.CreateLogger<GroupResolutionJob>(), settings.Workflow.AgentTenantId,
                    issues: parts.Issues),
                offboarding,
                // Each job on its own: a failing one is logged and on the issues list, and the agent still advances the requests.
                parts.Stores, parts.Issues, loggers.CreateLogger<WorkflowWorker>());
        });
    })
    .Build();

host.Run();
