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
    /// <summary>v1.1 (F3): Prod above Read after both approvals: waits for a ProdApprover's decision on the ProdWrite escalation.</summary>
    AwaitingProdApproval,
    Rejected,
    WithIdTeam,
    WithRoleTeam,
    Verifying,
    Completed,
    FailedNeedsDev,
    /// <summary>v1.1 (F5): the person was offboarded; the request was closed without granting anything further.</summary>
    Closed,
}

/// <summary>Who the request is waiting on right now.</summary>
public enum WaitingOn { None, Agent, Requester, Owner, Security, IdTeam, RoleTeam, User, Developer, ProdApprover }

/// <summary>Owner, then Security; Prod (v1.1, F3) only for Prod above Read, after both, from a ProdApprover.</summary>
public enum ApprovalStage { Owner, Security, Prod }

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
    /// <summary>The Azure role (definition GUID) and scope the approvers saw; the only role the agent may assign (v2.1).</summary>
    public string? AzureRoleDefinitionId { get; init; }
    public string? AzureScope { get; init; }
    /// <summary>
    /// Every Azure role (role × resource) the approvers saw for the group; the only roles the agent may assign (v2.1).
    /// Empty on scopes written before 21 Oct 2026: then the single <see cref="AzureRoleDefinitionId"/> pair applies.
    /// </summary>
    public IReadOnlyList<AzureRoleGrant> AzureRoles { get; init; } = [];
    /// <summary>The environment the access is for (e.g. Dev, PreProd).</summary>
    public string? Environment { get; init; }
    public GroupState GroupState { get; init; }
    /// <summary>Entra cross-check: the group already exists (reuse it), even if the sheet says "to be created".</summary>
    public bool? GroupExistsInEntra { get; init; }
    public required string SheetFingerprint { get; init; }
    public IReadOnlyList<string> RiskFlags { get; init; } = [];
    /// <summary>AI summary carried with every approval request: what, why, group, role, risk flags, review results.</summary>
    public required string Summary { get; init; }

    /// <summary>The approved Azure roles: <see cref="AzureRoles"/>, else the single legacy pair, else none.</summary>
    public IReadOnlyList<AzureRoleGrant> Grants() =>
        AzureRoles.Count > 0 ? AzureRoles
        : AzureRoleDefinitionId is not null && AzureScope is not null
            ? [new AzureRoleGrant { RoleName = BuiltInRoles.NameOf(AzureRoleDefinitionId) ?? AzureRoleDefinitionId, RoleDefinitionId = AzureRoleDefinitionId, Scope = AzureScope }]
            : [];

    /// <summary>Same group and role, incl. every Azure role: any difference needs new approvals.</summary>
    public bool SameAccess(ReviewedScope other)
    {
        var a = Grants();
        var b = other.Grants();
        return string.Equals(EntraGroup, other.EntraGroup, StringComparison.OrdinalIgnoreCase)
               && string.Equals(Role, other.Role, StringComparison.OrdinalIgnoreCase)
               && string.Equals(RoleScope ?? "", other.RoleScope ?? "", StringComparison.OrdinalIgnoreCase)
               && (Environment is null ? other.Environment is null : Domain.Environments.Same(Environment, other.Environment))
               && a.Count == b.Count && a.All(g => b.Any(g.SameAs));
    }
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
    /// <summary>v2.1: what the AI agent did itself after both approvals, or why it left the work to the team.</summary>
    public AgentWork? Agent { get; init; }
}

/// <summary>Agent provisioning record (ids and flags only). <see cref="Manual"/> set = the team does it instead.</summary>
public sealed record AgentWork
{
    public required DateTimeOffset At { get; init; }
    public string? UserObjectId { get; init; }
    public bool UserCreated { get; init; }
    public string? GroupObjectId { get; init; }
    public bool GroupCreated { get; init; }
    public bool MemberAdded { get; init; }
    /// <summary>The user is a member of the group (added now or already).</summary>
    public bool Membership { get; init; }
    public bool MappingUpdated { get; init; }
    public bool RoleAssigned { get; init; }
    /// <summary>Done: nothing left for the agent in this task.</summary>
    public bool Completed { get; init; }
    public string? Manual { get; init; }
    /// <summary>First refusal of the agent's write (permission not in its token yet); retried until <see cref="WorkflowSettings.PermissionWait"/>.</summary>
    public DateTimeOffset? RefusedSince { get; init; }
    /// <summary>Agent TAP: when the one-time pass the agent created starts and expires (metadata only; never the pass).</summary>
    public DateTimeOffset? TapIssuedAt { get; init; }
    public DateTimeOffset? TapExpiresAt { get; init; }
    /// <summary>Agent TAP: when the sign-in details mail went to the verified enterprise mail (sent once, never stored).</summary>
    public DateTimeOffset? SignInDetailsSentAt { get; init; }
    /// <summary>F2: where it went, masked (<see cref="TapRecipient.Masked"/>): the people inventory's EnterpriseEmail, checked against Entra.</summary>
    public string? SignInDetailsSentTo { get; init; }
    /// <summary>The initial password was in that mail (the account was created in the same run).</summary>
    public bool PasswordMailed { get; init; }
    /// <summary>Agent TAP: why the pass was left to the ID team (the rest of the agent's work still counts).</summary>
    public string? TapLeft { get; init; }
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

    public bool IsOpen => Status is not (RequestStatus.Completed or RequestStatus.Rejected or RequestStatus.FailedNeedsDev or RequestStatus.Returned or RequestStatus.Closed);

    /// <summary>Nothing more will happen to it (F5 closes every request that is not).</summary>
    public bool IsFinal => Status is RequestStatus.Completed or RequestStatus.Rejected or RequestStatus.Closed;

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
        RequestStatus.AwaitingProdApproval => "Awaiting Prod approval",
        RequestStatus.Rejected => "Rejected",
        RequestStatus.WithIdTeam => "With ID Team",
        RequestStatus.WithRoleTeam => "With Role Team",
        RequestStatus.Verifying => "Verifying",
        RequestStatus.Completed => "Completed",
        RequestStatus.FailedNeedsDev => "Failed-Needs-Dev",
        RequestStatus.Closed => "Closed – offboarded",
        _ => s.ToString(),
    };

    public static string Label(WaitingOn w) => w switch
    {
        WaitingOn.None => "—",
        WaitingOn.Agent => "AI agent",
        WaitingOn.IdTeam => "ID team",
        WaitingOn.RoleTeam => "Role team",
        WaitingOn.User => "New user",
        WaitingOn.ProdApprover => "ProdApprover",
        _ => w.ToString(),
    };

    /// <summary>The 7-step flow, stated to every requester upfront. Step 7 never waits for the user's sign-in (22 Oct 2026).</summary>
    public static readonly IReadOnlyList<string> FlowOverview =
    [
        "1. Requester submits the request",
        "2. AI review (mapping sheet + response document)",
        "3. Owner approval",
        "4. Security approval (Prod above Read: then a ProdApprover)",
        "5. ID team creates user/group (+ TAP)",
        "6. Role assignment team assigns roles",
        "7. AI verifies the access, notifies the requester (Completed), audit",
    ];
}
