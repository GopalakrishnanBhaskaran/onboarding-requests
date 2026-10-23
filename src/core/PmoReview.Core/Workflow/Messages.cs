using System.Globalization;
using PmoReview.Core.Common;
using PmoReview.Core.Domain;

namespace PmoReview.Core.Workflow;

/// <summary>
/// Who gets what. Addresses come from the request (requester, Owner, user) and the project's contact inventory (Security,
/// ID team, role assignment team, PMO, Operators; edited on the Contact inventory page). <see cref="Notifier"/> turns each
/// into a plain mail address (guest sign-in forms included) and drops anything that is not one.
/// </summary>
public static class Recipients
{
    /// <summary>The person who submitted the request (asked for the account): their sign-in name.</summary>
    public static IEnumerable<string?> Requester(AccessRequest r) => [r.CreatedByName];
    public static IEnumerable<string?> Owner(AccessRequest r) => [r.Owner];
    /// <summary>The joiner: the enterprise mail address given as Enterprise ID (the new tenant account has no mailbox yet).</summary>
    public static IEnumerable<string?> User(AccessRequest r) => [Services.DomainIds.MailOf(r.EnterpriseId)];
    public static IEnumerable<string?> Security(Project? p) => p?.Contacts.Security ?? [];
    public static IEnumerable<string?> IdTeam(Project? p) => p?.Contacts.IdTeam ?? [];
    public static IEnumerable<string?> RoleTeam(Project? p) => p?.Contacts.RoleTeam ?? [];
    public static IEnumerable<string?> Pmo(Project? p) => p?.Contacts.Pmo ?? [];
    public static IEnumerable<string?> Operators(Project? p) => p?.Contacts.Operators ?? [];

    /// <summary>
    /// The one completion mail: the requester first, then Security, PMO, the ID team and the role assignment team (one mail
    /// instead of three: the Azure-managed mail domain sends at most 10 mails an hour, F70). Never the Owner.
    /// </summary>
    public static IEnumerable<string?> Completion(AccessRequest r, Project? p) =>
        Requester(r).Concat(Security(p)).Concat(Pmo(p)).Concat(IdTeam(p)).Concat(RoleTeam(p));

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

/// <summary>
/// Plain-text notification templates. Short and direct. Never contain a TAP or other secret — except
/// <see cref="SignInDetails"/>, which is sent once directly and stored only with both secrets masked.
/// </summary>
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
        (s.AgentPrivileged ? "This includes rows flagged privileged, except privileged administrator roles (Owner, Contributor, User Access Administrator and the like). " : "") +
        (s.AgentTap && s.MailEnabled
            ? "For a new account the agent also mails the joiner a one-time Temporary Access Pass, the initial password and the MFA set-up steps. " +
              $"The ID team handles what the agent leaves to it ({Left(s)}). "
            : $"The ID team issues the Temporary Access Pass and handles what the agent leaves to it ({Left(s)}). ") +
        $"The role assignment team assigns the {(s.AgentRoleAssignment ? "other " : "")}roles. PMO owns the mapping sheet and is informed only.";

    private static string Left(WorkflowSettings s) => s.AgentPrivileged ? "guests, privileged administrator roles, exceptions" : "guests, privileged access, exceptions";

    public static string Short(AccessRequest r) => r.Id.Length > 8 ? r.Id[..8] : r.Id;

    public static string Who(AccessRequest r) => $"{r.FirstName} {r.LastName} ({r.DomainId}{(r.Guest ? ", guest" : "")})";

    public static string What(AccessRequest r) => $"{Who(r)}: '{r.AccessLevel}' on '{r.Resource}'{(r.Environment is null ? "" : $" ({r.Environment})")}";

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

    public static string RoleTeamInstructions(ResponseRecord record) => record.AzureRoles.Count == 0
        ? $"From the response document: user {record.UserId}, group {record.EntraGroup}, role {record.Role}{(record.RoleScope is null ? "" : $" at {record.RoleScope}")}.\n" +
          "Assign exactly this role to the group (no extra roles, none missing), then record what you assigned here; that updates the response document."
        : $"From the response document: user {record.UserId}, group {record.EntraGroup}{(record.Environment is null ? "" : $" ({record.Environment})")}. " +
          $"Assign exactly these {record.AzureRoles.Count} Azure role(s) to the group (no extra roles, none missing):\n" +
          Bullets(record.AzureRoles.Select(g => $"{g.RoleName} on {g.Where}{(g.ResourceType is null ? "" : $" ({g.ResourceType})")}")) +
          "\nThen record exactly what you assigned here (one line per role and scope); that updates the response document.";

    /// <summary>After the agent created the user, group and membership (v2.1).</summary>
    public static (string, string) Provisioned(AccessRequest r, ReviewedScope scope, string done, string next, WorkflowSettings s) => (
        $"AI agent provisioned request {Short(r)}: {r.DomainId}",
        $"After the Owner and Security approvals, the AI agent provisioned {What(r)}.\n\n{done}\n{next}\n\n" +
        $"Approved access:\n{scope.Summary}\n\n{Link(s, r)}");

    public static (string, string) TeamTask(AccessRequest r, TeamTask task, WorkflowSettings s) => (
        $"{Team(task.Team)} task: request {Short(r)}",
        $"{task.Instructions}\n\nPick it up and record completion in the app: {Link(s, r)}");

    public static (string, string) TaskReturned(AccessRequest r, TeamTask task, WorkflowSettings s) => (
        $"Returned to {Team(task.Team)}: request {Short(r)}",
        $"The AI verification of your report for {What(r)} found differences:\n{task.LastDifference}\n\nFix them (or update the mapping sheet, which triggers a new review) and record completion again: {Link(s, r)}");

    /// <summary>
    /// Agent TAP: the one mail with the joiner's sign-in name, initial password and one-time TAP, and how to set up MFA.
    /// Called twice: with the real values for the mail that is sent, and with <see cref="SecretMasker.Mask"/> for the copy
    /// the app keeps. Without the password (account created in an earlier run) the user sets one after MFA.
    /// </summary>
    public static (string Subject, string Body) SignInDetails(AccessRequest r, ReviewedScope scope, Project? project, string upn, string? password, string pass,
        DateTimeOffset expires, WorkflowSettings s)
    {
        var tenant = project?.TenantName ?? project?.Name;
        var grants = scope.Grants();
        var access = grants.Count == 0 ? $"{scope.Role}{(scope.RoleScope is null ? "" : $" at {scope.RoleScope}")}"
            : string.Join(", ", grants.Select(g => g.RoleName).Distinct(StringComparer.OrdinalIgnoreCase));
        var lines = new List<string>
        {
            $"Hello {r.FirstName},",
            "",
            $"Your account{(tenant is null ? "" : $" for {tenant}")}{(r.Environment is null ? "" : $" ({r.Environment})")} was created after the Owner and Security approved request {Short(r)}.",
            "",
            $"Sign-in name: {upn}",
            password is null ? "Temporary password (not included): see step 6" : $"Temporary password: {password}",
            $"Temporary Access Pass: {pass}",
            $"The Temporary Access Pass works once and expires {Time(expires)}.",
            "",
            "Set up multi-factor authentication (MFA) first, before the pass expires:",
            "1. Open a private (InPrivate / Incognito) browser window and go to https://aka.ms/mysecurityinfo",
            "2. Enter your sign-in name. When asked, enter the Temporary Access Pass (not the password).",
            "3. Select \"Add sign-in method\" and choose Microsoft Authenticator. Install the Microsoft Authenticator app on your phone, add a work or school account, and scan the QR code. Approve the test notification. Finish within 10 minutes of signing in.",
            "4. Still on Security info: delete the Temporary Access Pass entry if you see one.",
            "5. Sign out and close the private window.",
            password is null
                ? "6. Set your password at https://aka.ms/sspr (it uses the Microsoft Authenticator you just added). If that page is not available to you, ask the ID team."
                : "6. Sign in again at https://myapps.microsoft.com with your sign-in name and the temporary password. Choose a new password when asked and approve the sign-in in Microsoft Authenticator.",
            "",
            $"Your access: '{r.AccessLevel}' on '{r.Resource}' through group {scope.EntraGroup} ({access}). You get another mail when the access is ready.",
            "",
            "Keep this mail private and delete it once you have set up your account. Nobody will ask you for the pass or the password.",
            "If the pass expired before you used it, ask the ID team for a new one.",
            "",
            $"Request {Short(r)} · requested by {r.CreatedByName}.",
        };
        return ($"Your new account{(tenant is null ? "" : $" for {tenant}")}: sign-in details and MFA set-up", string.Join("\n", lines));
    }

    public static (string, string) AccessReady(AccessRequest r, ReviewedScope scope, Project? project = null, bool signInDetailsMailed = false)
    {
        var grants = scope.Grants();
        var roles = grants.Count == 0 ? $"role {scope.Role}{(scope.RoleScope is null ? "" : $" at {scope.RoleScope}")}"
            : $"Azure roles:\n{Bullets(grants.Select(g => $"{g.RoleName} on {g.ResourceName ?? g.Scope}"))}";
        var tenant = project?.TenantName is { } t ? $"Tenant: {t}\n" : "";
        return (
            $"Your access is ready: {r.AccessLevel} on {r.Resource}{(r.Environment is null ? "" : $" ({r.Environment})")}",
            $"Hello {r.FirstName},\n\nYour access '{r.AccessLevel}' on '{r.Resource}' is ready.\n\n" +
            tenant +
            (r.Environment is null ? "" : $"Environment: {r.Environment}\n") +
            $"Your sign-in name (domain-specific ID): {r.DomainId}\n" +
            $"Group: {scope.EntraGroup}\n{roles}\n\n" +
            (signInDetailsMailed
                ? "How to sign in: use the sign-in details mailed to you earlier (subject \"Your new account…: sign-in details and MFA set-up\"). " +
                  "If you have not set up Microsoft Authenticator yet, follow the steps in that mail first.\n\n"
                : "How to sign in: go to https://mysignins.microsoft.com. The ID team gives you a one-time Temporary Access Pass directly and separately; " +
                  "it is never sent by mail. After the first sign-in, register a permanent sign-in method (Microsoft Authenticator or a passkey).\n\n") +
            $"Request {Short(r)} · requested by {r.CreatedByName} · approved by the Owner and Security.");
    }

    /// <summary>
    /// The one completion mail (requester, Security, PMO, ID team, role assignment team): what was approved, who did each
    /// step, the verified access and the time per stage. No action needed from anyone.
    /// </summary>
    public static (string, string) Completed(AccessRequest r, ReviewedScope scope, WorkflowState flow, string timings, WorkflowSettings s)
    {
        var grants = scope.Grants();
        var roles = grants.Count == 0 ? $"Role: {scope.Role}{(scope.RoleScope is null ? "" : $" at {scope.RoleScope}")}"
            : $"Azure roles:\n{Bullets(grants.Select(g => $"{g.RoleName} on {g.ResourceName ?? g.Scope}"))}";
        var owner = flow.CurrentApproval(ApprovalStage.Owner);
        var security = flow.CurrentApproval(ApprovalStage.Security);
        string By(TeamTask? t, string team) => t?.ReportedBy is null ? team : t.ReportedBy == WorkflowAgent.AgentName ? "the AI agent" : $"{team} ({t.ReportedBy})";
        // The agent created the account even when the ID team then issued the pass (agent TAP off, or left to the team).
        string IdBy(TeamTask? t) => t?.Agent is not { Completed: true } a ? By(t, "ID team")
            : "the AI agent" + (a.SignInDetailsSentAt is not null ? "; sign-in details mailed to the joiner"
                : t.ReportedBy is { } by && by != WorkflowAgent.AgentName ? $"; Temporary Access Pass by the ID team ({by})" : "");
        return (
            $"Completed: request {Short(r)} — {r.DomainId}",
            $"Request {Short(r)} for {What(r)} is complete. The AI agent verified the account, the group membership and the roles.\n\n" +
            $"Sign-in name: {r.DomainId}\nGroup: {scope.EntraGroup}\n{roles}\n\n" +
            $"Requested by: {r.CreatedByName}\n" +
            $"Approved by: Owner {owner?.ByName} ({Time(owner?.At)}); Security {security?.ByName} ({Time(security?.At)})\n" +
            $"User, group and membership: {IdBy(flow.IdTeamTask)}\n" +
            $"Roles: {By(flow.RoleTeamTask, "role assignment team")}\n" +
            $"Mapping row: {scope.MappingRowId}\n\n" +
            $"The joiner was mailed at {Services.DomainIds.MailOf(r.EnterpriseId) ?? r.EnterpriseId} that the access is ready. No action needed.\n\n" +
            $"Time per stage:\n{timings}\n\n{Link(s, r)}");
    }

    public static (string, string) SecurityAlert(AccessRequest r, string who, string userId, ApprovalStage stage, DateTimeOffset at) => (
        $"Security alert: unauthorized approval attempt on request {Short(r)}",
        $"{who} (user id {userId}) tried to respond at the {stage} stage on {Time(at)} but is not {(stage == ApprovalStage.Owner ? "the named Owner" : "a Security approver")}. " +
        "The response was ignored and logged.");

    public static string Team(AppRole team) => team == AppRole.IdTeam ? "ID team" : "Role assignment team";

    public static string Bullets(IEnumerable<string> items) => string.Join("\n", items.Select(i => $"- {i}"));

    public static string Time(DateTimeOffset? t) => t?.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture) ?? "—";

    public static string Hours(TimeSpan t) => t.TotalHours >= 1 ? $"{t.TotalHours:0.#} h" : $"{Math.Max(1, (int)t.TotalMinutes)} min";
}
