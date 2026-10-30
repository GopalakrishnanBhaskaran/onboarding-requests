using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PmoReview.Adapters;
using PmoReview.Adapters.Entra;
using PmoReview.Adapters.Outbound;
using PmoReview.Adapters.Rbac;
using PmoReview.Adapters.Review;
using PmoReview.Adapters.Storage;
using PmoReview.Core.Common;
using PmoReview.Core.Directory;
using PmoReview.Core.Storage;
using PmoReview.Core.Workflow;

// The AI agent's background worker (paid component). Refuses to start unless paid services are explicitly allowed
// (CLAUDE.md rule 11). Managed identity only. The agent never approves. Entra and Azure RBAC are read only unless the
// approved v2.1 provisioning is switched on (AGENT_PROVISIONING / AGENT_ROLE_ASSIGNMENT): then, only after the Owner and
// Security approvals, it creates the user, group and membership and assigns allow-listed Azure roles. With the approved
// AGENT_TAP (and a mail sender) it also mails a new account's one-time TAP and initial password to the joiner, once.
string? Env(string name) => Environment.GetEnvironmentVariable(name);
bool On(string name) => string.Equals(Env(name), "true", StringComparison.OrdinalIgnoreCase);
if (!On("ALLOW_PAID_SERVICES"))
    throw new InvalidOperationException("The workflow worker is a paid component. Set ALLOW_PAID_SERVICES=true only in the approved environment.");
if (!Uri.TryCreate(Env("COSMOS_ENDPOINT"), UriKind.Absolute, out var cosmosEndpoint))
    throw new InvalidOperationException("COSMOS_ENDPOINT is required (managed identity; no keys).");
foreach (var retired in new[] { "ENTRA_AUTO_ASSIGN", "ENTRA_PROVISION_JOINERS", "ENTRA_AUTO_CREATE_PACKAGES" })
    if (On(retired))
        throw new InvalidOperationException($"{retired}=true is retired (Phase 2 access packages). v2.1 provisioning is AGENT_PROVISIONING / AGENT_ROLE_ASSIGNMENT, after both approvals.");

var credential = new Azure.Identity.DefaultAzureCredential();
var settings = PilotSettings.FromEnvironment(Env);
HttpClient Http() => new() { Timeout = TimeSpan.FromSeconds(30) };

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices(services =>
    {
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton(_ => CosmosDataStores.Create(
            CosmosDataStores.CreateClientWithIdentity(cosmosEndpoint, credential), Env("COSMOS_DATABASE") ?? "pmoreview"));
        services.AddSingleton<IDirectoryReader>(_ => new GraphDirectoryReader(Http(), credential));
        services.AddSingleton<INotificationSender>(_ => Env("NOTIFY_SENDER") switch
        {
            "GraphMail" => new GraphMailSender(Http(), credential, Env("NOTIFY_FROM") ?? throw new InvalidOperationException("NOTIFY_SENDER=GraphMail needs NOTIFY_FROM (the shared sender mailbox).")),
            // Azure Communication Services Email with the worker's managed identity (deploy_free.sh email, approval-gated).
            "AcsEmail" => new AcsEmailSender(
                Uri.TryCreate(Env("ACS_ENDPOINT"), UriKind.Absolute, out var acs) ? acs : throw new InvalidOperationException("NOTIFY_SENDER=AcsEmail needs ACS_ENDPOINT (https://<name>.communication.azure.com)."),
                credential,
                Env("NOTIFY_FROM") ?? throw new InvalidOperationException("NOTIFY_SENDER=AcsEmail needs NOTIFY_FROM (DoNotReply@<managed domain>).")),
            _ => new OutboxOnlySender(),
        });
        services.AddSingleton(sp =>
        {
            var loggers = sp.GetRequiredService<ILoggerFactory>();
            IRoleAssignmentReader? roles = Env("ROLE_READER") == "Arm" ? new ArmRoleAssignmentReader(Http(), credential) : null;
            IAiAssistant? assistant = settings.AiReports && settings.ReviewModelProvider == AzureOpenAIReviewModel.ProviderName
                ? new AzureOpenAIAssistant(ReviewModelFactory.Chat(settings, logger: loggers.CreateLogger("AzureOpenAI")))
                : null;
            IDirectoryWriter? writer = settings.Workflow.AgentProvisioning ? new GraphDirectoryWriter(Http(), credential) : null;
            IRoleAssignmentWriter? roleWriter = settings.Workflow.AgentRoleAssignment
                ? new ArmRoleAssignmentWriter(Http(), credential, settings.Workflow.RoleAssignAllowedRoleIds)
                : null;
            // Agent TAP: the sign-in details are sent directly (never through the stored outbox), same sender and test redirect.
            IDirectMail? directMail = settings.Workflow.AgentTap && writer is not null
                ? new DirectMail(sp.GetRequiredService<INotificationSender>(), settings.Workflow.MailRedirectTo, settings.Workflow.AppEnvironment)
                : null;
            return PilotApp.BuildWorkflow(sp.GetRequiredService<DataStores>(), settings, sp.GetRequiredService<IClock>(), loggers,
                directory: sp.GetRequiredService<IDirectoryReader>(), roles: roles, assistant: assistant, writer: writer, roleWriter: roleWriter, directMail: directMail);
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
            return new WorkflowWorker(parts.Agent,
                new NotificationDispatcher(parts.Stores, sender, parts.Issues, clock, loggers.CreateLogger<NotificationDispatcher>(),
                    redirectTo: settings.Workflow.MailRedirectTo, environment: settings.Workflow.AppEnvironment,
                    // Within the sender's limits (F70); with the agent TAP, 2 mails an hour stay free for the sign-in details.
                    limits: settings.Workflow.MailLimits, reserve: settings.Workflow.AgentTap ? 2 : 0),
                new AdoSync(parts.Stores, tracker, parts.Issues, clock, loggers.CreateLogger<AdoSync>()),
                new DailySummaryJob(parts.Stores, parts.Notifier, clock, int.TryParse(Env("DAILY_SUMMARY_HOUR_UTC"), out var hour) ? hour : 6, assistant),
                // v1.1 (F1): mapping group names resolved in Entra (read-only), unresolved groups escalated.
                new GroupResolutionJob(parts.Stores, sp.GetRequiredService<IDirectoryReader>(), clock, loggers.CreateLogger<GroupResolutionJob>(), settings.Workflow.AgentTenantId));
        });
    })
    .Build();

host.Run();
