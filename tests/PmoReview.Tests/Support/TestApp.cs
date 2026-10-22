using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PmoReview.Adapters;
using PmoReview.Adapters.Dispatch;
using PmoReview.Adapters.Storage;
using PmoReview.Core.Common;
using PmoReview.Core.Directory;
using PmoReview.Core.Domain;
using PmoReview.Core.Http;
using PmoReview.Core.Review;
using PmoReview.Core.Security;
using PmoReview.Core.Storage;
using PmoReview.Core.Workflow;

namespace PmoReview.Tests.Support;

public sealed class FixedClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset UtcNow { get; set; } = now;
    public void Advance(TimeSpan by) => UtcNow += by;
}

/// <summary>
/// Seeded SAMPLE dataset (in-memory) behind the real API host, called the way SWA calls it, plus the worker's
/// composition (same stores and clock) with a fake read-only Entra directory: <see cref="RunWorker"/> is one worker cycle.
/// </summary>
public sealed class TestApp
{
    public const string Alpha = "SAMPLE-ALPHA";
    public const string Beta = "SAMPLE-BETA";
    public const string Alice = "sample-user-alice"; // Requester, Alpha
    public const string Olivia = "sample-user-olivia"; // Owner, Alpha (named on the default request)
    public const string Otto = "sample-user-otto";   // Owner, Alpha (not named)
    public const string Sam = "sample-user-sam";     // Security, Alpha
    public const string Dual = "sample-user-dual";   // Owner + Security, Alpha
    public const string Ivan = "sample-user-ivan";   // ID team, Alpha
    public const string Rose = "sample-user-rose";   // Role assignment team, Alpha
    public const string Paul = "sample-user-paul";   // PMO, Alpha
    public const string Oscar = "sample-user-oscar"; // Operator, Alpha
    public const string Bob = "sample-user-bob";     // Requester, Beta
    public const string Bella = "sample-user-bella"; // Owner + Security + PMO, Beta
    public const string Stranger = "sample-user-nobody"; // signed in, no access rows

    public const string DefaultUser = "sample.e1001@sample.invalid";
    public const string DefaultGroup = "SAMPLE-GRP-ALPHA-DBW-ENG-USER";
    public const string DefaultRole = "SAMPLE Workspace User";
    public const string DefaultScope = "SAMPLE dbw-alpha-dev";

    public static string SignIn(string user) => $"{user}@sample.invalid";

    public DataStores Stores { get; }
    public FixedClock Clock { get; } = new(new DateTimeOffset(2026, 10, 17, 9, 0, 0, TimeSpan.Zero));
    public ApiHost Host { get; }
    /// <summary>The API's own parts (no directory reader, like the SWA-managed API).</summary>
    public WorkflowParts Api { get; }
    /// <summary>The worker's parts: same stores and clock, with the read-only directory and role reader.</summary>
    public WorkflowParts Worker { get; }
    public FakeDirectory Directory { get; }
    public FakeRoles Roles { get; }
    /// <summary>v2.1 agent provisioning (used only when the settings switch it on; the worker holds it, never the API).</summary>
    public FakeDirectoryWriter Writer { get; }
    public FakeRoleWriter RoleWriter { get; }
    /// <summary>Agent TAP: the sign-in details mail (sent directly, never stored).</summary>
    public FakeDirectMail Mail { get; }
    public FakeTracker Tracker { get; } = new();
    public PilotSettings Settings { get; }

    private TestApp(PilotSettings settings, IReviewModel? model, IQueueClient? queue, IDirectoryReader? apiDirectory, DataStores? stores = null)
    {
        Stores = stores ?? InMemoryDataStores.Create();
        SampleDataLoader.SeedAsync(Stores, SampleDataLoader.Read(Path.Combine(AppContext.BaseDirectory, "sample-data.json"), SampleAccessScope)).GetAwaiter().GetResult();
        Settings = settings;
        Directory = FakeDirectory.SeededFromSample();
        Roles = new FakeRoles();
        Api = PilotApp.BuildWorkflow(Stores, settings, Clock, NullLoggerFactory.Instance, model, queue, apiDirectory);
        Host = PilotApp.Build(Stores, settings, Clock, NullLoggerFactory.Instance, model, queue, apiDirectory);
        Writer = new FakeDirectoryWriter(Directory, () => Clock.UtcNow);
        RoleWriter = new FakeRoleWriter(Roles);
        Mail = new FakeDirectMail(settings.Workflow.MailRedirectTo);
        Worker = PilotApp.BuildWorkflow(Stores, settings with { Dispatcher = "Inline" }, Clock, NullLoggerFactory.Instance, model, directory: Directory, roles: Roles,
            writer: settings.Workflow.AgentProvisioning ? Writer : null, roleWriter: settings.Workflow.AgentRoleAssignment ? RoleWriter : null,
            directMail: settings.Workflow.AgentTap ? Mail : null);
    }

    /// <summary>The approved access resource group of the tests (fills the SAMPLE PEST rows' Azure role scopes).</summary>
    public const string SampleAccessScope = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-sample-access";
    public const string Pest = "SAMPLE-PEST";
    public const string Vestacy = "SAMPLE-VESTACY";

    public static TestApp Create() => new(new PilotSettings(), null, null, null);

    /// <summary>Same wiring on other stores (the Cosmos emulator contract test, or a restart with new settings), seeded with the SAMPLE data.</summary>
    public static TestApp On(DataStores stores, PilotSettings? settings = null) => new(settings ?? new PilotSettings(), null, null, null, stores);

    /// <summary>Real wiring with an optional scripted reviewer, settings, queue, or a directory reader in the API process.</summary>
    public static TestApp With(IReviewModel? model = null, PilotSettings? settings = null, IQueueClient? queue = null, IDirectoryReader? apiDirectory = null) =>
        new(settings ?? new PilotSettings(), model, queue, apiDirectory);

    /// <summary>One worker cycle: agent steps for open requests (with the Entra reader), then notifications.</summary>
    public async Task<WorkerSummary> RunWorker()
    {
        var worker = new WorkflowWorker(Worker.Agent,
            new NotificationDispatcher(Stores, new OutboxOnlySender(), Worker.Issues, Clock, NullLogger<NotificationDispatcher>.Instance),
            new AdoSync(Stores, Tracker, Worker.Issues, Clock, NullLogger<AdoSync>.Instance),
            new DailySummaryJob(Stores, Worker.Notifier, Clock));
        return await worker.RunOnceAsync("test-cycle", CancellationToken.None);
    }

    public static object Intake(string enterpriseId = "sample.e1001@sample-corp.invalid", string resource = "SAMPLE Databricks Engineering", string accessLevel = "User",
        string? justification = null, string owner = "sample-user-olivia@sample.invalid", bool guest = false, string first = "Sample", string last = "Joiner",
        string? environment = null) => new
    {
        enterpriseId,
        firstName = first,
        lastName = last,
        resource,
        accessLevel,
        environment,
        justification = justification ?? "SAMPLE: joins the data engineering team to build and run ingestion pipelines in the dev workspace.",
        owner,
        guest,
    };

    public Task<Resp> Submit(object? body = null, string user = Alice, string project = Alpha) =>
        Call(user, "POST", $"projects/{project}/requests", body ?? Intake());

    public async Task<string> SubmitId(object? body = null, string user = Alice, string project = Alpha)
    {
        var r = await Submit(body, user, project);
        if (r.Status != 201) throw new InvalidOperationException($"submit failed {r.Status} {r.Body}");
        return r.Request.GetProperty("id").GetString()!;
    }

    public Task<Resp> View(string requestId, string user = Alice, string project = Alpha) =>
        Call(user, "GET", $"projects/{project}/requests/{requestId}");

    public async Task<AccessRequest> Request(string requestId, string project = Alpha) =>
        (await Stores.Requests.GetAsync(project, requestId))!.Value;

    public async Task<RequestStatus> Status(string requestId) => (await Request(requestId)).Flow!.Status;

    public async Task<Resp> Approve(string requestId, string user, string decision = "Approved", string? remarks = null, string? runId = null, string project = Alpha)
    {
        runId ??= (await Request(requestId, project)).Flow?.Scope?.ReviewRunId;
        return await Call(user, "POST", $"projects/{project}/requests/{requestId}/approvals", new { decision, remarks, reviewRunId = runId });
    }

    /// <summary>Submit and drive the request to the given stage through the real API and worker.</summary>
    public async Task<string> To(RequestStatus target, object? body = null)
    {
        var id = await SubmitId(body);
        await RunWorker();
        if (target == RequestStatus.AwaitingOwner) return Expect(id, await Status(id), target);
        Ok(await Approve(id, Olivia));
        if (target == RequestStatus.AwaitingSecurity) return Expect(id, await Status(id), target);
        Ok(await Approve(id, Sam));
        if (target == RequestStatus.WithIdTeam) return Expect(id, await Status(id), target);
        Directory.AddUser(DefaultUser);
        Directory.AddMember(DefaultGroup, DefaultUser);
        Directory.SetTap(DefaultUser, new TapMethod(Clock.UtcNow, Clock.UtcNow, 60, true, true, "EnabledByPolicy"));
        Ok(await ReportIdTeam(id, IdReport()));
        await RunWorker();
        if (target == RequestStatus.WithRoleTeam) return Expect(id, await Status(id), target);
        Roles.Assign(Directory.GroupId(DefaultGroup), DefaultRole, DefaultScope);
        Ok(await ReportRoleTeam(id, RoleReport()));
        await RunWorker();
        return Expect(id, await Status(id), target);
    }

    private static string Expect(string id, RequestStatus actual, RequestStatus expected) =>
        actual == expected ? id : throw new InvalidOperationException($"expected {expected}, got {actual}");

    private static void Ok(Resp r)
    {
        if (r.Status is < 200 or > 299) throw new InvalidOperationException($"call failed {r.Status} {r.Body}");
    }

    public object IdReport(string upn = DefaultUser, string group = DefaultGroup, bool userCreated = true, bool added = true, object? tap = null, bool includeTap = true) => new
    {
        userPrincipalName = upn,
        groupName = group,
        userCreated,
        groupCreated = false,
        addedToGroup = added,
        mappingSheetUpdated = false,
        tap = includeTap ? tap ?? new { issuedAt = Clock.UtcNow, expiresAt = Clock.UtcNow.AddMinutes(60), oneTime = true, issuedBy = SignIn(Ivan) } : null,
        notes = "SAMPLE: done.",
    };

    public static object RoleReport(string role = DefaultRole, string? scope = DefaultScope) => new
    {
        assignedRoles = new[] { new { role, scope } },
        notes = "SAMPLE: assigned.",
    };

    public Task<Resp> ReportIdTeam(string requestId, object body, string user = Ivan, string project = Alpha) =>
        Call(user, "POST", $"projects/{project}/requests/{requestId}/tasks/id-team/report", body);

    public Task<Resp> ReportRoleTeam(string requestId, object body, string user = Rose, string project = Alpha) =>
        Call(user, "POST", $"projects/{project}/requests/{requestId}/tasks/role-team/report", body);

    /// <summary>A call whose platform principal carries a given sign-in name (userDetails), e.g. a guest UPN.</summary>
    public Task<Resp> CallAs(string user, string userDetails, string method, string path, object? body = null) =>
        Send(method, path, new Dictionary<string, string>
        {
            [ClientPrincipalParser.HeaderName] = ClientPrincipalParser.Encode(new ClientPrincipal("aad", user, userDetails, ["anonymous", "authenticated"])),
        }, body is null ? null : AppJson.Serialize(body));

    /// <summary>The user signs in, registers a permanent method, and the ID team deletes the TAP.</summary>
    public void UserCompletesFirstSignIn(string upn = DefaultUser)
    {
        Directory.SignInNow(upn, Clock.UtcNow);
        Directory.SetMethods(upn, ["password", "microsoftAuthenticator"]);
        Directory.SetTap(upn, null);
    }

    public async Task<IReadOnlyList<Notification>> Notifications(string? requestId = null, string project = Alpha) =>
        (await Stores.Notifications.ListAsync(project)).Select(n => n.Value).Where(n => requestId is null || n.RequestId == requestId).OrderBy(n => n.CreatedAt).ToList();

    public async Task<IReadOnlyList<Issue>> Issues(string? requestId = null, string project = Alpha) =>
        (await Stores.Issues.ListAsync(project)).Select(n => n.Value).Where(n => requestId is null || n.RequestId == requestId).ToList();

    public static string Principal(string userId, string provider = "aad", params string[] roles) =>
        ClientPrincipalParser.Encode(new ClientPrincipal(provider, userId, SignIn(userId),
            roles.Length == 0 ? ["anonymous", "authenticated"] : roles));

    public Task<Resp> Call(string? user, string method, string path, object? body = null, string? ifMatch = null, string? rawBody = null) =>
        CallWith(CancellationToken.None, user, method, path, body, ifMatch, rawBody);

    public Task<Resp> CallWith(CancellationToken ct, string? user, string method, string path, object? body = null, string? ifMatch = null, string? rawBody = null)
    {
        var headers = new Dictionary<string, string>();
        if (user is not null) headers[ClientPrincipalParser.HeaderName] = Principal(user);
        if (ifMatch is not null) headers["If-Match"] = ifMatch;
        return Send(method, path, headers, rawBody ?? (body is null ? null : AppJson.Serialize(body)), ct);
    }

    public async Task<Resp> Send(string method, string path, Dictionary<string, string> headers, string? body, CancellationToken ct = default)
    {
        var r = await Host.HandleAsync(new ApiRequest(method, path, headers, body, Guid.NewGuid().ToString("n")), ct);
        return new Resp(r.Status, r.SerializedBody, r.Headers);
    }
}

public sealed record Resp(int Status, string? Body, IReadOnlyDictionary<string, string>? Headers)
{
    public JsonElement Json => JsonDocument.Parse(Body ?? "null").RootElement;
    public JsonElement Value => Json.GetProperty("value");
    public string ETag => Json.GetProperty("etag").GetString()!;
    public string ErrorCode => Json.GetProperty("error").GetProperty("code").GetString()!;
    /// <summary>RequestView.request.value</summary>
    public JsonElement Request => Json.GetProperty("request").GetProperty("value");
    public string StatusName => Json.GetProperty("status").GetProperty("status").GetString()!;
}
