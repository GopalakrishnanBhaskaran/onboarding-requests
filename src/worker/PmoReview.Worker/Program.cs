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
// (CLAUDE.md rule 10). Managed identity only. Entra and Azure RBAC are READ ONLY: the agent never approves, creates
// or assigns anything.
string? Env(string name) => Environment.GetEnvironmentVariable(name);
bool On(string name) => string.Equals(Env(name), "true", StringComparison.OrdinalIgnoreCase);
if (!On("ALLOW_PAID_SERVICES"))
    throw new InvalidOperationException("The workflow worker is a paid component. Set ALLOW_PAID_SERVICES=true only in the approved environment.");
if (!Uri.TryCreate(Env("COSMOS_ENDPOINT"), UriKind.Absolute, out var cosmosEndpoint))
    throw new InvalidOperationException("COSMOS_ENDPOINT is required (managed identity; no keys).");
foreach (var retired in new[] { "ENTRA_AUTO_ASSIGN", "ENTRA_PROVISION_JOINERS", "ENTRA_AUTO_CREATE_PACKAGES" })
    if (On(retired))
        throw new InvalidOperationException($"{retired}=true is retired: the AI agent never creates or assigns. The ID team and the role assignment team do that.");

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
        services.AddSingleton(sp =>
        {
            var loggers = sp.GetRequiredService<ILoggerFactory>();
            IRoleAssignmentReader? roles = Env("ROLE_READER") == "Arm" ? new ArmRoleAssignmentReader(Http(), credential) : null;
            IAiAssistant? assistant = settings.AiReports && settings.ReviewModelProvider == AzureOpenAIReviewModel.ProviderName
                ? new AzureOpenAIAssistant(ReviewModelFactory.Chat(settings, logger: loggers.CreateLogger("AzureOpenAI")))
                : null;
            return PilotApp.BuildWorkflow(sp.GetRequiredService<DataStores>(), settings, sp.GetRequiredService<IClock>(), loggers,
                directory: sp.GetRequiredService<IDirectoryReader>(), roles: roles, assistant: assistant);
        });
        services.AddSingleton(sp =>
        {
            var parts = sp.GetRequiredService<WorkflowParts>();
            var loggers = sp.GetRequiredService<ILoggerFactory>();
            var clock = sp.GetRequiredService<IClock>();
            INotificationSender sender = Env("NOTIFY_SENDER") == "GraphMail"
                ? new GraphMailSender(Http(), credential, Env("NOTIFY_FROM") ?? throw new InvalidOperationException("NOTIFY_SENDER=GraphMail needs NOTIFY_FROM (the shared sender mailbox)."))
                : new OutboxOnlySender();
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
                new NotificationDispatcher(parts.Stores, sender, parts.Issues, clock, loggers.CreateLogger<NotificationDispatcher>()),
                new AdoSync(parts.Stores, tracker, parts.Issues, clock, loggers.CreateLogger<AdoSync>()),
                new DailySummaryJob(parts.Stores, parts.Notifier, clock, int.TryParse(Env("DAILY_SUMMARY_HOUR_UTC"), out var hour) ? hour : 6, assistant));
        });
    })
    .Build();

host.Run();
