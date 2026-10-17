using PmoReview.Core.Domain;

namespace PmoReview.Core.Workflow;

/// <summary>Request statuses, in flow order. Labels: <see cref="StatusText"/>.</summary>
public enum RequestStatus
{
    Submitted,
    AiReview,
    Returned,
    AwaitingOwner,
    AwaitingSecurity,
    Rejected,
    WithIdTeam,
    WithRoleTeam,
    Verifying,
    Completed,
    FailedNeedsDev,
}

/// <summary>Who the request is waiting on right now.</summary>
public enum WaitingOn { None, Agent, Requester, Owner, Security, IdTeam, RoleTeam, User, Developer }

public enum ApprovalStage { Owner, Security }

public enum ApprovalDecision { Approved, Rejected }

/// <summary>
/// One approval decision. Valid only from the named Owner (Owner stage) or a Security member (Security stage), through
/// the app (the tracked channel), tied to the request id and the review run the approver saw.
/// </summary>
public sealed record Approval
{
    public required ApprovalStage Stage { get; init; }
    public required ApprovalDecision Decision { get; init; }
    public required string By { get; init; }
    public required string ByName { get; init; }
    public required DateTimeOffset At { get; init; }
    public string? Remarks { get; init; }
    public required string ReviewRunId { get; init; }
    public string Channel { get; init; } = "App";
    /// <summary>The reviewed group or role changed after this approval; it no longer counts.</summary>
    public bool Superseded { get; init; }
}

/// <summary>
/// What a passing review pinned: the single mapping row, its group and role, and the sheet fingerprint. Approvals and
/// team tasks refer to exactly this; if the sheet changes, the request is reviewed again before it continues.
/// </summary>
public sealed record ReviewedScope
{
    public required string ReviewRunId { get; init; }
    public required string MappingRowId { get; init; }
    public required int MappingRowVersion { get; init; }
    public required string EntraGroup { get; init; }
    public required string Role { get; init; }
    public string? RoleScope { get; init; }
    public GroupState GroupState { get; init; }
    /// <summary>Entra cross-check: the group already exists (reuse it), even if the sheet says "to be created".</summary>
    public bool? GroupExistsInEntra { get; init; }
    public required string SheetFingerprint { get; init; }
    public IReadOnlyList<string> RiskFlags { get; init; } = [];
    /// <summary>AI summary carried with every approval request: what, why, group, role, risk flags, review results.</summary>
    public required string Summary { get; init; }

    public bool SameAccess(ReviewedScope other) =>
        string.Equals(EntraGroup, other.EntraGroup, StringComparison.OrdinalIgnoreCase)
        && string.Equals(Role, other.Role, StringComparison.OrdinalIgnoreCase)
        && string.Equals(RoleScope ?? "", other.RoleScope ?? "", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Temporary Access Pass metadata. The pass itself never reaches this app, its logs, ADO, mail or prompts.</summary>
public sealed record TapRecord(DateTimeOffset IssuedAt, DateTimeOffset ExpiresAt, bool OneTime, string IssuedBy, DateTimeOffset RecordedAt);

/// <summary>What the ID team reports after creating the user ID / group, adding the membership and issuing the TAP.</summary>
public sealed record IdTeamReport
{
    public required string UserPrincipalName { get; init; }
    public string? UserObjectId { get; init; }
    public required string GroupName { get; init; }
    public string? GroupObjectId { get; init; }
    public bool GroupCreated { get; init; }
    /// <summary>The ID team created a new account (it then needs a one-time TAP for the first sign-in).</summary>
    public bool UserCreated { get; init; }
    public bool AddedToGroup { get; init; }
    public bool MappingSheetUpdated { get; init; }
    public TapRecord? Tap { get; init; }
    public string? Notes { get; init; }
}

/// <summary>What the role assignment team reports. The response document is updated from it.</summary>
public sealed record RoleTeamReport
{
    public required IReadOnlyList<AssignedRole> AssignedRoles { get; init; }
    public string? Notes { get; init; }
}

public enum TeamTaskState { Open, Reported, Returned, Verified, Cancelled }

/// <summary>
/// A task for the ID team or the role assignment team. <see cref="Id"/> is the idempotency key (request id + step).
/// The agent creates it, verifies the team's report and returns it with the exact difference on a mismatch.
/// </summary>
public sealed record TeamTask
{
    public required string Id { get; init; }
    public required AppRole Team { get; init; }
    public required TeamTaskState State { get; init; }
    public required string Instructions { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    /// <summary>When the team became responsible: created, or last returned. SLA counts from here.</summary>
    public required DateTimeOffset SinceAt { get; init; }
    public DateTimeOffset? PickedUpAt { get; init; }
    public string? PickedUpBy { get; init; }
    public DateTimeOffset? ReportedAt { get; init; }
    public string? ReportedBy { get; init; }
    public IdTeamReport? IdReport { get; init; }
    public RoleTeamReport? RoleReport { get; init; }
    public int ReturnCount { get; init; }
    public string? LastDifference { get; init; }
    public DateTimeOffset? RemindedAt { get; init; }
    public DateTimeOffset? EscalatedAt { get; init; }
    /// <summary>AI verification of the last report.</summary>
    public IReadOnlyList<CheckResult> Checks { get; init; } = [];
}

public sealed record StageEntry(RequestStatus Status, WaitingOn WaitingOn, DateTimeOffset EnteredAt, DateTimeOffset? LeftAt);

/// <summary>Audit trail entry. <see cref="Note"/> is masked; never a secret or a TAP.</summary>
public sealed record WorkflowEvent(DateTimeOffset At, string Actor, string Step, string Note);

/// <summary>Azure DevOps work item linked to the request id.</summary>
public sealed record AdoLink
{
    public int? WorkItemId { get; init; }
    public string? Url { get; init; }
    public DateTimeOffset? SyncedAt { get; init; }
    /// <summary>Number of workflow events already written to the work item.</summary>
    public int SyncedEvents { get; init; }
    public bool Closed { get; init; }
    public string? Error { get; init; }
}

/// <summary>Workflow part of a request. Written only by <see cref="WorkflowService"/> and <see cref="WorkflowAgent"/> (ETag).</summary>
public sealed record WorkflowState
{
    public const string AgentActor = "agent";

    public required RequestStatus Status { get; init; }
    public WaitingOn WaitingOn { get; init; }
    public string? ReviewRunId { get; init; }
    public ReviewedScope? Scope { get; init; }
    /// <summary>Set when a mapping-sheet change sent an in-flight request back to AI review: where it resumes if nothing material changed.</summary>
    public RequestStatus? ResumeStatus { get; init; }
    public IReadOnlyList<StageEntry> Stages { get; init; } = [];
    public IReadOnlyList<Approval> Approvals { get; init; } = [];
    public TeamTask? IdTeamTask { get; init; }
    public TeamTask? RoleTeamTask { get; init; }
    /// <summary>Stage 6: login test, permanent method registered, TAP deleted.</summary>
    public IReadOnlyList<CheckResult> FinalChecks { get; init; } = [];
    public DateTimeOffset? RemindedAt { get; init; }
    public DateTimeOffset? EscalatedAt { get; init; }
    public AdoLink? Ado { get; init; }
    /// <summary>Latest return, rejection or failure reason (masked).</summary>
    public string? LastReason { get; init; }
    /// <summary>Attempts per idempotency key (request id + step). Auto-fix retries stop at 3.</summary>
    public IReadOnlyDictionary<string, int> Attempts { get; init; } = new Dictionary<string, int>();
    public IReadOnlyList<WorkflowEvent> Events { get; init; } = [];
    public DateTimeOffset? CompletedAt { get; init; }

    public bool IsOpen => Status is not (RequestStatus.Completed or RequestStatus.Rejected or RequestStatus.FailedNeedsDev or RequestStatus.Returned);

    public DateTimeOffset StageSince => Stages.Count > 0 ? Stages[^1].EnteredAt : DateTimeOffset.MinValue;

    public Approval? CurrentApproval(ApprovalStage stage) =>
        Approvals.LastOrDefault(a => a.Stage == stage && !a.Superseded);

    public static WorkflowState Start(DateTimeOffset now, string actor) => new()
    {
        Status = RequestStatus.Submitted,
        WaitingOn = WaitingOn.Agent,
        Stages = [new StageEntry(RequestStatus.Submitted, WaitingOn.Agent, now, null)],
        Events = [new WorkflowEvent(now, actor, "submit", "Request submitted.")],
    };

    /// <summary>Moves to a new status: closes the current stage entry, opens the next one and records the event.</summary>
    public WorkflowState To(RequestStatus status, WaitingOn waitingOn, DateTimeOffset now, string actor, string step, string note)
    {
        var stages = Stages.ToList();
        if (stages.Count > 0 && stages[^1].LeftAt is null) stages[^1] = stages[^1] with { LeftAt = now };
        stages.Add(new StageEntry(status, waitingOn, now, null));
        return this with
        {
            Status = status,
            WaitingOn = waitingOn,
            Stages = stages,
            RemindedAt = null,
            EscalatedAt = null,
            CompletedAt = status == RequestStatus.Completed ? now : CompletedAt,
            Events = [.. Events, new WorkflowEvent(now, actor, step, note)],
        };
    }

    /// <summary>Same status, new owner of the wait (e.g. a task returned to the ID team). Splits the stage entry for timing.</summary>
    public WorkflowState Waiting(WaitingOn waitingOn, DateTimeOffset now)
    {
        if (waitingOn == WaitingOn) return this;
        var stages = Stages.ToList();
        if (stages.Count > 0 && stages[^1].LeftAt is null) stages[^1] = stages[^1] with { LeftAt = now };
        stages.Add(new StageEntry(Status, waitingOn, now, null));
        return this with { WaitingOn = waitingOn, Stages = stages };
    }

    public WorkflowState Log(DateTimeOffset now, string actor, string step, string note) =>
        this with { Events = [.. Events, new WorkflowEvent(now, actor, step, note)] };

    public int AttemptsFor(string key) => Attempts.TryGetValue(key, out var n) ? n : 0;

    public WorkflowState WithAttempt(string key)
    {
        var next = new Dictionary<string, int>(Attempts) { [key] = AttemptsFor(key) + 1 };
        return this with { Attempts = next };
    }
}

public static class StatusText
{
    public static string Label(RequestStatus s) => s switch
    {
        RequestStatus.Submitted => "Submitted",
        RequestStatus.AiReview => "AI Review",
        RequestStatus.Returned => "Returned",
        RequestStatus.AwaitingOwner => "Awaiting Owner",
        RequestStatus.AwaitingSecurity => "Awaiting Security",
        RequestStatus.Rejected => "Rejected",
        RequestStatus.WithIdTeam => "With ID Team",
        RequestStatus.WithRoleTeam => "With Role Team",
        RequestStatus.Verifying => "Verifying",
        RequestStatus.Completed => "Completed",
        RequestStatus.FailedNeedsDev => "Failed-Needs-Dev",
        _ => s.ToString(),
    };

    public static string Label(WaitingOn w) => w switch
    {
        WaitingOn.None => "—",
        WaitingOn.Agent => "AI agent",
        WaitingOn.IdTeam => "ID team",
        WaitingOn.RoleTeam => "Role team",
        WaitingOn.User => "New user",
        _ => w.ToString(),
    };

    /// <summary>The 7-step flow, stated to every requester upfront.</summary>
    public static readonly IReadOnlyList<string> FlowOverview =
    [
        "1. Requester submits the request",
        "2. AI review (mapping sheet + response document)",
        "3. Owner approval",
        "4. Security approval",
        "5. ID team creates user/group (+ TAP)",
        "6. Role assignment team assigns roles",
        "7. AI verification, notifications, audit",
    ];
}
