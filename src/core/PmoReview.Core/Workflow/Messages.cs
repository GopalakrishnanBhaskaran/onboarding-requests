using System.Globalization;
using PmoReview.Core.Domain;

namespace PmoReview.Core.Workflow;

/// <summary>Who gets what. Addresses come from the request (requester, Owner, user) and the project's contacts.</summary>
public static class Recipients
{
    public static IEnumerable<string?> Requester(AccessRequest r) => [r.CreatedByName];
    public static IEnumerable<string?> Owner(AccessRequest r) => [r.Owner];
    public static IEnumerable<string?> User(AccessRequest r) => [r.DomainId];
    public static IEnumerable<string?> Security(Project? p) => p?.Contacts.Security ?? [];
    public static IEnumerable<string?> IdTeam(Project? p) => p?.Contacts.IdTeam ?? [];
    public static IEnumerable<string?> RoleTeam(Project? p) => p?.Contacts.RoleTeam ?? [];
    public static IEnumerable<string?> Pmo(Project? p) => p?.Contacts.Pmo ?? [];
    public static IEnumerable<string?> Operators(Project? p) => p?.Contacts.Operators ?? [];

    public static IEnumerable<string?> For(WaitingOn w, AccessRequest r, Project? p) => w switch
    {
        WaitingOn.Owner => Owner(r),
        WaitingOn.Security => Security(p),
        WaitingOn.IdTeam => IdTeam(p),
        WaitingOn.RoleTeam => RoleTeam(p),
        WaitingOn.User => User(r),
        WaitingOn.Requester => Requester(r),
        _ => Operators(p),
    };
}

/// <summary>Plain-text notification templates. Short and direct. Never contain a TAP or other secret.</summary>
public static class Messages
{
    public const string WhoDoesWhat =
        "Who does what: the AI agent reviews, routes, verifies and reports; it never approves, creates or assigns. " +
        "The named Owner approves the business need. Security approves risk and access level. " +
        "The ID team creates the user ID and group and issues the Temporary Access Pass. The role assignment team assigns roles. " +
        "PMO owns the mapping sheet and is informed only.";

    /// <summary>The same statement, true to the deployment: v2.1 agent provisioning on or off.</summary>
    public static string WhoDoesWhatFor(WorkflowSettings s) => !s.AgentProvisioning ? WhoDoesWhat :
        "Who does what: the AI agent reviews, routes, verifies and reports; it never approves. " +
        "The named Owner approves the business need. Security approves risk and access level. " +
        "Only after both approvals the AI agent creates the user ID, group and membership" +
        (s.AgentRoleAssignment ? " and assigns the approved Azure role where the mapping sheet allows it" : "") + ". " +
        "The ID team issues the Temporary Access Pass and handles what the agent leaves to it (guests, privileged access, exceptions). " +
        $"The role assignment team assigns the {(s.AgentRoleAssignment ? "other " : "")}roles. PMO owns the mapping sheet and is informed only.";

    public static string Short(AccessRequest r) => r.Id.Length > 8 ? r.Id[..8] : r.Id;

    public static string Who(AccessRequest r) => $"{r.FirstName} {r.LastName} ({r.DomainId}{(r.Guest ? ", guest" : "")})";

    public static string What(AccessRequest r) => $"{Who(r)}: '{r.AccessLevel}' on '{r.Resource}'";

    public static string Link(WorkflowSettings s, AccessRequest r) =>
        s.AppUrl is null ? $"Request ID {r.Id}" : $"{s.AppUrl.TrimEnd('/')}/#/requests/{Uri.EscapeDataString(r.Id)}";

    public static (string Subject, string Body) FlowOverview(AccessRequest r, WorkflowSettings s) => (
        $"Request {Short(r)} received — how it works",
        $"Your onboarding request for {What(r)} was received.\n\nFlow:\n{string.Join("\n", StatusText.FlowOverview)}\n\n{WhoDoesWhatFor(s)}\n\nTrack it: {Link(s, r)}");

    public static (string, string) Returned(AccessRequest r, IReadOnlyList<string> reasons, WorkflowSettings s) => (
        $"Request {Short(r)} returned — the AI review found gaps",
        $"Request for {What(r)} was not sent for approval.\n\nReasons:\n{Bullets(reasons)}\n\nCorrect the request (or the mapping sheet, PMO) and submit it again.\n{Link(s, r)}");

    public static (string, string) ApprovalRequested(AccessRequest r, ApprovalStage stage, ReviewedScope scope, Approval? owner, WorkflowSettings s) => (
        $"Approval needed ({stage}): request {Short(r)}",
        $"{(stage == ApprovalStage.Owner ? "As the named Owner, approve or reject the business need." : "As Security, approve or reject the risk and access level.")}\n\n" +
        $"AI review summary:\n{scope.Summary}\n" +
        AzureRoleLine(scope, s) +
        (owner is null ? "" : $"\nOwner approval: {owner.ByName} on {Time(owner.At)}{(owner.Remarks is null ? "" : $" — {owner.Remarks}")}\n") +
        $"\nRespond in the app (the only channel that counts): {Link(s, r)}");

    /// <summary>What the approvers approve when the row names an Azure role (and who assigns it).</summary>
    public static string AzureRoleLine(ReviewedScope scope, WorkflowSettings s) => scope.AzureRoleDefinitionId is null ? "" :
        $"Azure role: {scope.AzureRoleDefinitionId} at {scope.AzureScope} on group {scope.EntraGroup} — " +
        (s.AgentRoleAssignment ? "assigned by the AI agent only after both approvals.\n" : "assigned by the role assignment team.\n");

    public static (string, string) Reminder(AccessRequest r, string waitingFor, TimeSpan pending, WorkflowSettings s) => (
        $"Reminder: {waitingFor} pending for request {Short(r)}",
        $"Request for {What(r)} has waited {Hours(pending)} for {waitingFor}. Please act: {Link(s, r)}");

    public static (string, string) Escalation(AccessRequest r, string waitingFor, string? who, TimeSpan pending, WorkflowSettings s) => (
        $"Escalation: {waitingFor} past SLA for request {Short(r)}",
        $"Request for {What(r)} has waited {Hours(pending)} for {waitingFor}{(who is null ? "" : $" ({who})")}, past the SLA. A reminder was sent. " +
        $"Please follow up. Nothing is approved automatically.\n{Link(s, r)}");

    public static (string, string) Rejected(AccessRequest r, Approval a, WorkflowSettings s) => (
        $"Request {Short(r)} rejected by {a.Stage}",
        $"Request for {What(r)} was rejected by {a.Stage} ({a.ByName}) on {Time(a.At)}.\n\nRemarks: {a.Remarks}\n\nThe request is closed.\n{Link(s, r)}");

    public static string IdTeamInstructions(AccessRequest r, ReviewedScope scope, WorkflowState flow, int tapMaxMinutes)
    {
        var group = scope.GroupExistsInEntra == true
            ? (scope.GroupState == GroupState.ToBeCreated ? $"{scope.EntraGroup} — already exists in Entra: reuse it (do not create another)." : $"{scope.EntraGroup} — exists: use it.")
            : scope.GroupState == GroupState.ToBeCreated ? $"{scope.EntraGroup} — create it (flagged 'to be created'), then update the mapping sheet." : $"{scope.EntraGroup} — use it.";
        var owner = flow.CurrentApproval(ApprovalStage.Owner);
        var security = flow.CurrentApproval(ApprovalStage.Security);
        return $"User: {r.FirstName} {r.LastName}; Enterprise ID {r.EnterpriseId}; domain-specific ID {r.DomainId}{(r.Guest ? " (guest)" : "")}.\n" +
               $"Group: {group}\n" +
               "Steps: create the user ID if missing; add the user to the group; then issue a ONE-TIME Temporary Access Pass with the " +
               $"shortest lifetime (at most {tapMaxMinutes} minutes) and give it to the user directly, separately from the Enterprise ID. " +
               "Never put the pass in mail, tickets, chat or this app: record only issued time, expiry, one-time flag and issued-by.\n" +
               $"Approved summary:\n{scope.Summary}\n" +
               $"Approvals: Owner {owner?.ByName} ({Time(owner?.At)}); Security {security?.ByName} ({Time(security?.At)}).";
    }

    /// <summary>After the agent created the user, group and membership: only the TAP is left for the ID team.</summary>
    public static string IdTeamTapInstructions(AccessRequest r, ReviewedScope scope, WorkflowState flow, string done, int tapMaxMinutes)
    {
        var owner = flow.CurrentApproval(ApprovalStage.Owner);
        var security = flow.CurrentApproval(ApprovalStage.Security);
        return $"{done}\n" +
               $"Your step: issue a ONE-TIME Temporary Access Pass for {r.DomainId} with the shortest lifetime (at most {tapMaxMinutes} minutes) " +
               "and give it to the user directly, separately from the Enterprise ID. Never put the pass in mail, tickets, chat or this app: " +
               "record only issued time, expiry, one-time flag and issued-by.\n" +
               $"Approved summary:\n{scope.Summary}\n" +
               $"Approvals: Owner {owner?.ByName} ({Time(owner?.At)}); Security {security?.ByName} ({Time(security?.At)}).";
    }

    public static string RoleTeamInstructions(ResponseRecord record) =>
        $"From the response document: user {record.UserId}, group {record.EntraGroup}, role {record.Role}{(record.RoleScope is null ? "" : $" at {record.RoleScope}")}.\n" +
        "Assign exactly this role to the group (no extra roles, none missing), then record what you assigned here; that updates the response document.";

    public static (string, string) TeamTask(AccessRequest r, TeamTask task, WorkflowSettings s) => (
        $"{Team(task.Team)} task: request {Short(r)}",
        $"{task.Instructions}\n\nPick it up and record completion in the app: {Link(s, r)}");

    public static (string, string) TaskReturned(AccessRequest r, TeamTask task, WorkflowSettings s) => (
        $"Returned to {Team(task.Team)}: request {Short(r)}",
        $"The AI verification of your report for {What(r)} found differences:\n{task.LastDifference}\n\nFix them (or update the mapping sheet, which triggers a new review) and record completion again: {Link(s, r)}");

    public static (string, string) TapCleanup(AccessRequest r, WorkflowSettings s) => (
        $"Delete the Temporary Access Pass: request {Short(r)}",
        $"{Who(r)} has signed in and registered a permanent method, but a Temporary Access Pass is still on the account. " +
        $"Delete it in Entra (the agent never deletes anything). {Link(s, r)}");

    public static (string, string) AccessReady(AccessRequest r, ReviewedScope scope) => (
        "Your access is ready",
        $"Hello {r.FirstName},\n\nYour access '{r.AccessLevel}' on '{r.Resource}' is ready (role {scope.Role}).\n" +
        $"Sign in with {r.DomainId} at https://myapps.microsoft.com. The ID team gives you a one-time Temporary Access Pass directly and separately; " +
        "it is never sent by mail. After the first sign-in, register a permanent sign-in method (Microsoft Authenticator or a passkey).");

    public static (string, string) Completed(AccessRequest r, ReviewedScope scope, string timings) => (
        $"Completed: request {Short(r)}",
        $"{What(r)} — done and verified (sign-in, permanent method, TAP removed).\nGroup {scope.EntraGroup}, role {scope.Role}.\n\nTime per stage:\n{timings}");

    public static (string, string) PmoInformed(AccessRequest r, ReviewedScope scope) => (
        $"For information: request {Short(r)} completed",
        $"{What(r)} completed using mapping row {scope.MappingRowId} ({scope.EntraGroup} / {scope.Role}). No action needed.");

    public static (string, string) Closure(AccessRequest r) => (
        $"Closed: request {Short(r)}",
        $"{What(r)} is verified and closed. Thank you.");

    public static (string, string) SecurityAlert(AccessRequest r, string who, string userId, ApprovalStage stage, DateTimeOffset at) => (
        $"Security alert: unauthorized approval attempt on request {Short(r)}",
        $"{who} (user id {userId}) tried to respond at the {stage} stage on {Time(at)} but is not {(stage == ApprovalStage.Owner ? "the named Owner" : "a Security approver")}. " +
        "The response was ignored and logged.");

    public static string Team(AppRole team) => team == AppRole.IdTeam ? "ID team" : "Role assignment team";

    public static string Bullets(IEnumerable<string> items) => string.Join("\n", items.Select(i => $"- {i}"));

    public static string Time(DateTimeOffset? t) => t?.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture) ?? "—";

    public static string Hours(TimeSpan t) => t.TotalHours >= 1 ? $"{t.TotalHours:0.#} h" : $"{Math.Max(1, (int)t.TotalMinutes)} min";
}
