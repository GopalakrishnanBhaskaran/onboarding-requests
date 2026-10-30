using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using PmoReview.Core.Common;
using PmoReview.Core.Domain;
using PmoReview.Core.Security;
using PmoReview.Core.Storage;
using PmoReview.Core.Workflow;

namespace PmoReview.Core.Offboarding;

/// <summary>F5 output contract of <c>POST /api/offboarding/{enterpriseId}?mode=plan|execute</c>: exactly these fields.</summary>
public sealed record OffboardingView(string OffboardingId, string EnterpriseId, string ObjectId, string Mode, OffboardingStatus Status,
    IReadOnlyList<OffboardingStep> Steps, IReadOnlyList<string> Leftovers, IReadOnlyList<string> NotCovered, bool PmoNotified, string CorrelationId)
{
    public static OffboardingView Of(OffboardingRun r) =>
        new(r.Id, r.EnterpriseId, r.ObjectId ?? "", r.Mode, r.Status, r.Steps, r.Leftovers, r.NotCovered, r.PmoNotified, r.CorrelationId);
}

public sealed record OffboardingExecuteInput(string? OffboardingId);

/// <summary>
/// F5 (v1.1): the API side of offboarding. PMO or an Operator asks for a plan (dry run); the worker reads Entra and Azure
/// and fills it in (<c>Planning</c> → <c>Planned</c>, decision 2). Executing needs an explicit confirmation of that plan — by a
/// second person in a production tenant — and <c>AGENT_OFFBOARDING</c> on; the worker then executes, verifies and audits
/// (<see cref="OffboardingJob"/>). Repeats (a double click) return the run already there; nothing is done twice.
/// </summary>
public sealed partial class OffboardingService(DataStores stores, IClock clock, WorkflowSettings settings)
{
    public const string ModePlan = "plan", ModeExecute = "execute";

    /// <summary>Always listed as not covered (F5 scope; decision 17 for app role assignments).</summary>
    public static readonly IReadOnlyList<string> NotCoveredAlways =
    [
        "Databricks workspace users (not covered by v1.1: remove them in each workspace)",
        "Mailbox and OneDrive data (not covered by v1.1)",
        "Licences (not covered by v1.1)",
        "App role assignments (not read: needs Directory.Read.All, not granted; disabling the account stops sign-in to those apps)",
    ];

    public static bool ValidEnterpriseId(string? id) => id is not null && EnterpriseIdFormat().IsMatch(id);

    public async Task<(int Status, OffboardingView View)> PlanAsync(CallerContext caller, string? projectId, string enterpriseId, string cid, CancellationToken ct)
    {
        var project = await AuthoriseAsync(caller, projectId, ct);
        var upn = Upn(enterpriseId);
        if (Tenants.NotConnected(project, settings.AgentTenantId) is { } why)
            throw AppException.Conflict("tenant_not_connected", $"Offboarding is not available for {Tenants.Label(project)}: {why}.");
        var runs = await RunsAsync(project.ProjectId, upn, ct);
        if (runs.FirstOrDefault(r => r.Status is OffboardingStatus.Planning or OffboardingStatus.Planned or OffboardingStatus.Executing) is { } open)
            return (200, OffboardingView.Of(open)); // a double click, or a run still in progress: the same run

        var person = (await stores.PeopleInventory.GetAsync(project.ProjectId, Person.MakeId(upn), ct))?.Value;
        var now = clock.UtcNow;
        var run = new OffboardingRun
        {
            Id = $"off-{Ids.Hash($"{project.ProjectId}|{upn}|{runs.Count}")[..24]}", ProjectId = project.ProjectId, EnterpriseId = upn, DisplayName = person?.DisplayName,
            Mode = ModePlan, Status = OffboardingStatus.Planning, NotCovered = NotCoveredAlways, CorrelationId = cid,
            RequestedBy = caller.UserId, RequestedByName = caller.DisplayName, RequestedAt = now, UpdatedAt = now, IsSample = project.IsSample,
        };
        try
        {
            await stores.OffboardingRuns.CreateAsync(run, ct);
        }
        catch (DocumentConflictException)
        {
            return (200, OffboardingView.Of((await stores.OffboardingRuns.GetAsync(project.ProjectId, run.Id, ct))!.Value)); // two clicks at once
        }
        await AuditAsync(project, caller.UserId, "offboarding-plan-requested", upn, $"run {run.Id}", cid, ct);
        return (202, OffboardingView.Of(run));
    }

    public async Task<(int Status, OffboardingView View)> ExecuteAsync(CallerContext caller, string? projectId, string enterpriseId, OffboardingExecuteInput input,
        string cid, CancellationToken ct)
    {
        var project = await AuthoriseAsync(caller, projectId, ct);
        var upn = Upn(enterpriseId);
        if (string.IsNullOrWhiteSpace(input.OffboardingId)) throw AppException.Invalid("offboardingId (the plan to confirm) is required.");
        var v = await stores.OffboardingRuns.GetAsync(project.ProjectId, input.OffboardingId.Trim(), ct);
        if (v is null || !string.Equals(v.Value.EnterpriseId, upn, StringComparison.Ordinal)) throw AppException.NotFound("Offboarding plan");
        var run = v.Value;
        if (run.Status is OffboardingStatus.Executing or OffboardingStatus.Completed or OffboardingStatus.PartiallyCompleted or OffboardingStatus.Failed)
            return (200, OffboardingView.Of(run)); // confirmed already (a double click): nothing runs twice
        if (run.Status != OffboardingStatus.Planned)
            throw AppException.Conflict("plan_not_ready", "The plan is not ready yet (the AI agent reads Entra and Azure within a few minutes). Refresh and confirm then.");
        if (!settings.AgentOffboarding)
            throw AppException.Conflict("offboarding_off", "Offboarding changes are switched off (AGENT_OFFBOARDING). The plan shows what would change; the teams do it by hand.");
        if (clock.UtcNow - (run.PlannedAt ?? run.RequestedAt) > settings.OffboardPlanValidFor)
            throw AppException.Conflict("plan_expired", $"The plan is older than {settings.OffboardPlanValidFor.TotalHours:0} hours. Make a new plan and confirm that one.");
        // Four eyes in a production tenant: the person who asked for the plan does not confirm it.
        if (project.IsProductionTenant && (caller.UserId == run.RequestedBy || (run.RequestedByName is { Length: > 0 } n && caller.Is(n))))
            throw new AppException(ErrorKind.Forbidden, "requester_cannot_confirm", "In a production tenant a second person (PMO or Operator) confirms the offboarding, not the one who asked for the plan.");

        var now = clock.UtcNow;
        var confirmed = run with
        {
            Mode = ModeExecute, Status = OffboardingStatus.Executing, ConfirmedBy = caller.UserId, ConfirmedByName = caller.DisplayName, ConfirmedAt = now, UpdatedAt = now,
            CorrelationId = run.CorrelationId,
        };
        try
        {
            await stores.OffboardingRuns.ReplaceAsync(confirmed, v.ETag, ct);
        }
        catch (ConcurrencyException)
        {
            return (200, OffboardingView.Of((await stores.OffboardingRuns.GetAsync(project.ProjectId, run.Id, ct))!.Value)); // confirmed at the same moment
        }
        await AuditAsync(project, caller.UserId, "offboarding-confirmed", upn, $"run {run.Id}, plan {run.PlanHash}", cid, ct);
        return (202, OffboardingView.Of(confirmed));
    }

    /// <summary>The latest run for this person (or the named one).</summary>
    public async Task<OffboardingView> GetAsync(CallerContext caller, string? projectId, string enterpriseId, string? offboardingId, CancellationToken ct)
    {
        var project = await AuthoriseAsync(caller, projectId, ct);
        var runs = await RunsAsync(project.ProjectId, Upn(enterpriseId), ct);
        var run = offboardingId is null ? runs.FirstOrDefault() : runs.FirstOrDefault(r => r.Id == offboardingId);
        return OffboardingView.Of(run ?? throw AppException.NotFound("Offboarding"));
    }

    /// <summary>The steps' fingerprint: execute runs only the plan that was confirmed.</summary>
    public static string PlanHash(IEnumerable<OffboardingStep> steps) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", steps.Select(s => $"{s.Name}|{s.Target}|{s.Action}")))))[..32];

    private async Task<Project> AuthoriseAsync(CallerContext caller, string? projectId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(projectId)) throw AppException.Invalid("projectId is required.");
        caller.Require(projectId, Permission.Offboard);
        return (await stores.Projects.GetAsync(projectId, projectId, ct))?.Value ?? throw AppException.NotFound("Project");
    }

    private async Task<List<OffboardingRun>> RunsAsync(string projectId, string upn, CancellationToken ct) =>
        (await stores.OffboardingRuns.ListAsync(projectId, ct)).Select(r => r.Value).Where(r => r.EnterpriseId == upn)
            .OrderByDescending(r => r.RequestedAt).ToList();

    private static string Upn(string enterpriseId) =>
        ValidEnterpriseId(enterpriseId) ? enterpriseId.Trim().ToLowerInvariant() : throw AppException.Invalid("The Enterprise ID must be a UPN like jane.doe@company.com.");

    private async Task AuditAsync(Project project, string actor, string action, string target, string details, string cid, CancellationToken ct) =>
        await stores.AuditEvents.CreateAsync(new AuditEvent
        {
            Id = AuditEvent.NewId(clock.UtcNow), ProjectId = project.ProjectId, At = clock.UtcNow, Actor = actor, Action = action, Target = target,
            CorrelationId = cid, Details = details, IsSample = project.IsSample,
        }, ct);

    /// <summary>A UPN (guest forms with #EXT# included).</summary>
    [GeneratedRegex("^[A-Za-z0-9._%+'#-]{1,113}@[A-Za-z0-9-]+(\\.[A-Za-z0-9-]+)+$")]
    private static partial Regex EnterpriseIdFormat();
}
