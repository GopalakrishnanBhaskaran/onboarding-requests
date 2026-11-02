using PmoReview.Core.Directory;
using PmoReview.Core.Domain;
using PmoReview.Core.Review;
using PmoReview.Core.Sources;

namespace PmoReview.Core.Workflow;

public static class VerifyCodes
{
    public const string UserId = "ID_USER_NAME";
    public const string GroupName = "ID_GROUP_NAME";
    public const string GroupConvention = "ID_GROUP_CONVENTION";
    public const string MembershipReported = "ID_MEMBERSHIP_REPORTED";
    public const string MappingUpdated = "ID_MAPPING_UPDATED";
    public const string Tap = "ID_TAP";
    public const string EntraUser = "ENTRA_USER_EXISTS";
    public const string EntraGroup = "ENTRA_GROUP_EXISTS";
    public const string EntraMembership = "ENTRA_MEMBERSHIP";
    public const string EntraTap = "ENTRA_TAP";
    public const string RoleResponseDoc = "ROLE_RESPONSE_DOC";
    public const string RoleExact = "ROLE_EXACT";
    public const string RoleInAzure = "ROLE_IN_AZURE";
    /// <summary>
    /// Final verification: account, group membership and exact roles. The user's first sign-in, permanent method and TAP
    /// removal are no longer waited for (Gokul, 22 Oct 2026: the request completes once the access is verified).
    /// </summary>
    public const string FinalAccess = "FINAL_ACCESS";
}

/// <summary>Verification outcome. <see cref="Pending"/>: something cannot be checked yet (no Entra reader here: the worker checks it).</summary>
public sealed record Verification(IReadOnlyList<CheckResult> Checks)
{
    public bool Failed => Checks.Any(c => c.Status == CheckStatus.Fail);
    public bool Pending => !Failed && Checks.Any(c => c.Status == CheckStatus.Pending);
    public bool Passed => !Failed && !Pending;
    public string Differences => string.Join("\n", Checks.Where(c => c.Status == CheckStatus.Fail).Select(c => $"- {c.Detail}"));
    public bool Has(string code) => Checks.Any(c => c.Code == code && c.Status == CheckStatus.Fail);
}

/// <summary>
/// AI verification of what the teams did (Stages 4–6). Reads only: the report, the sources of truth and, when available,
/// Entra and role assignments. It never fixes anything itself; a mismatch goes back to the team with the exact difference.
/// </summary>
public sealed class Verifier(IDirectoryReader? directory, IRoleAssignmentReader? roles, WorkflowSettings settings, Databricks.DatabricksConfig? databricks = null)
{
    public bool CanReadEntra => directory is not null;

    private CheckResult NoEntra(string code, string group, string? notConnected = null) => notConnected is not null
        ? new(code, group, CheckStatus.NotApplicable, $"Not verified by the agent: {notConnected}. The team's report is the record.")
        : new(code, group,
            settings.AllowUnverifiedEntra ? CheckStatus.NotApplicable : CheckStatus.Pending,
            settings.AllowUnverifiedEntra ? "Not verified in Entra (local development: no directory reader)." : "Verified in Entra by the background agent within a few minutes.");

    /// <summary>Stage 4: user ID, group, membership, mapping sheet updated, TAP rules.</summary>
    /// <param name="notConnected">The project's tenant is not the agent's (tenant separation): Entra is not read.</param>
    public async Task<Verification> IdTeamAsync(AccessRequest r, ReviewedScope scope, WorkflowState flow, IdTeamReport report,
        IReadOnlyList<MappingRow> sheet, string groupPattern, CancellationToken ct, string? notConnected = null)
    {
        const string G = CheckGroups.IdTeam;
        var checks = new List<CheckResult>();
        void Add(string code, bool ok, string pass, string fail) => checks.Add(new CheckResult(code, G, ok ? CheckStatus.Pass : CheckStatus.Fail, ok ? pass : fail));

        Add(VerifyCodes.UserId, MappingSheet.Same(report.UserPrincipalName, r.DomainId),
            $"User ID {report.UserPrincipalName} matches the domain-specific ID.",
            $"Expected user ID {r.DomainId}, reported {report.UserPrincipalName}.");
        Add(VerifyCodes.GroupName, MappingSheet.Same(report.GroupName, scope.EntraGroup),
            $"Group {report.GroupName} matches the reviewed group.",
            $"Expected group {scope.EntraGroup} (reviewed and approved), reported {report.GroupName}. Fix the group name, or update the mapping sheet — the request is then reviewed again and needs new approvals.");
        Add(VerifyCodes.GroupConvention, ReviewChecks.GroupNameFollows(report.GroupName, groupPattern),
            "Group name follows the naming convention.", $"Group name {report.GroupName} does not follow the naming convention ({groupPattern}).");
        Add(VerifyCodes.MembershipReported, report.AddedToGroup,
            "The user was added to the group.", "The report does not confirm the user was added to the group.");

        if (scope.GroupState == GroupState.ToBeCreated)
        {
            var row = sheet.FirstOrDefault(x => x.RowId == scope.MappingRowId);
            var updated = row is not null && row.GroupState == GroupState.Exists && MappingSheet.Same(row.EntraGroup, scope.EntraGroup);
            Add(VerifyCodes.MappingUpdated, updated,
                $"Mapping sheet row {scope.MappingRowId} now shows group {scope.EntraGroup} as existing.",
                $"Mapping sheet row {scope.MappingRowId} still says group {scope.EntraGroup} is 'to be created' (or names another group). Update the mapping sheet.");
        }
        else
        {
            checks.Add(new CheckResult(VerifyCodes.MappingUpdated, G, CheckStatus.NotApplicable, "The group already existed; no mapping sheet update needed."));
        }

        checks.Add(TapRules(report, flow));

        if (directory is null || notConnected is not null)
        {
            checks.Add(NoEntra(VerifyCodes.EntraUser, G, notConnected));
            checks.Add(NoEntra(VerifyCodes.EntraGroup, G, notConnected));
            checks.Add(NoEntra(VerifyCodes.EntraMembership, G, notConnected));
            return new Verification(checks);
        }

        var user = await directory.FindUserAsync(r.DomainId!, ct);
        Add(VerifyCodes.EntraUser, user is not null, $"User {r.DomainId} exists in Entra.", $"No user {r.DomainId} in Entra.");
        var groups = await directory.FindGroupsAsync(scope.EntraGroup, ct);
        Add(VerifyCodes.EntraGroup, groups.Count == 1 && groups[0].SecurityEnabled,
            $"Security group {scope.EntraGroup} exists in Entra.",
            groups.Count == 0 ? $"No group {scope.EntraGroup} in Entra." : groups.Count > 1 ? $"{groups.Count} groups are named {scope.EntraGroup} in Entra." : $"{scope.EntraGroup} is not a security group.");
        if (user is not null && groups.Count == 1)
        {
            var member = await directory.IsMemberAsync(groups[0].ObjectId, user.ObjectId, ct);
            Add(VerifyCodes.EntraMembership, member, $"{r.DomainId} is a member of {scope.EntraGroup} in Entra.", $"{r.DomainId} is not a member of {scope.EntraGroup} in Entra.");
            if (report.Tap is not null)
            {
                var methods = await directory.GetAuthMethodsAsync(user.ObjectId, ct);
                checks.Add(EntraTap(methods.Tap, TapLimit(flow)));
            }
        }
        else
        {
            checks.Add(new CheckResult(VerifyCodes.EntraMembership, G, CheckStatus.NotApplicable, "Not checked: user or group not found."));
        }
        return new Verification(checks);
    }

    /// <summary>TAP rules on the reported metadata: after all approvals and the membership, one-time, shortest lifetime.</summary>
    public CheckResult TapRules(IdTeamReport report, WorkflowState flow)
    {
        const string G = CheckGroups.IdTeam;
        var tap = report.Tap;
        if (tap is null)
            return report.UserCreated
                ? new CheckResult(VerifyCodes.Tap, G, CheckStatus.Fail, "A new account needs a one-time Temporary Access Pass for the first sign-in; record its metadata (never the pass).")
                : new CheckResult(VerifyCodes.Tap, G, CheckStatus.NotApplicable, "No Temporary Access Pass recorded (existing account with its own sign-in method).");
        var security = flow.CurrentApproval(ApprovalStage.Security);
        var problems = new List<string>();
        if (security is null || security.Decision != ApprovalDecision.Approved || tap.IssuedAt < Minute(security.At))
            problems.Add("The TAP was issued before all approvals were complete.");
        if (!report.AddedToGroup) problems.Add("The TAP must be issued only after the group membership.");
        if (!tap.OneTime) problems.Add("The TAP must be one-time use.");
        var lifetime = (tap.ExpiresAt - tap.IssuedAt).TotalMinutes;
        var limit = TapLimit(flow);
        if (lifetime > limit) problems.Add($"TAP lifetime is {lifetime:0} minutes; use the shortest lifetime (at most {limit}).");
        return problems.Count == 0
            ? new CheckResult(VerifyCodes.Tap, G, CheckStatus.Pass, $"TAP metadata recorded: issued {Messages.Time(tap.IssuedAt)}, expires {Messages.Time(tap.ExpiresAt)}, one-time, by {tap.IssuedBy}.")
            : new CheckResult(VerifyCodes.Tap, G, CheckStatus.Fail, string.Join(" ", problems));
    }

    /// <summary>
    /// Longest TAP lifetime allowed: the ID team's limit, or — for a pass the agent mailed (recorded only by the agent in
    /// its own work record, never from a team's form) — the agent TAP lifetime, since the joiner reads it from mail.
    /// </summary>
    private int TapLimit(WorkflowState flow) => flow.IdTeamTask?.Agent?.SignInDetailsSentAt is not null
        ? Math.Max(settings.TapMaxLifetimeMinutes, settings.AgentTapLifetimeMinutes)
        : settings.TapMaxLifetimeMinutes;

    /// <summary>TAP times are recorded to the minute (datetime-local), so comparisons with approval times use minute precision.</summary>
    internal static DateTimeOffset Minute(DateTimeOffset t) => new(t.Ticks - t.Ticks % TimeSpan.TicksPerMinute, t.Offset);

    private static CheckResult EntraTap(TapMethod? tap, int limit)
    {
        const string G = CheckGroups.IdTeam;
        if (tap is null) return new CheckResult(VerifyCodes.EntraTap, G, CheckStatus.Pass, "No TAP on the account now (already used or removed).");
        var problems = new List<string>();
        if (tap.IsUsableOnce == false) problems.Add("Entra shows the TAP is not one-time use.");
        if (tap.LifetimeMinutes > limit) problems.Add($"Entra shows a TAP lifetime of {tap.LifetimeMinutes} minutes (maximum {limit}).");
        return problems.Count == 0
            ? new CheckResult(VerifyCodes.EntraTap, G, CheckStatus.Pass, "Entra shows a one-time TAP within the lifetime limit.")
            : new CheckResult(VerifyCodes.EntraTap, G, CheckStatus.Fail, string.Join(" ", problems));
    }

    /// <summary>Stage 5: the assignment matches the approved roles exactly (no extra, none missing).</summary>
    public async Task<Verification> RoleTeamAsync(ReviewedScope scope, ResponseRecord? record, RoleTeamReport report, CancellationToken ct, string? notConnected = null)
    {
        const string G = CheckGroups.RoleTeam;
        var checks = new List<CheckResult>();
        var expected = WorkflowAgent.ExpectedRoles(scope);

        if (record is null)
        {
            checks.Add(new CheckResult(VerifyCodes.RoleResponseDoc, G, CheckStatus.Fail, "The response document has no record for this request."));
            return new Verification(checks);
        }
        var docOk = record.State == ResponseState.Assigned && MappingSheet.Same(record.EntraGroup, scope.EntraGroup) && MappingSheet.Same(record.Role, scope.Role);
        checks.Add(new CheckResult(VerifyCodes.RoleResponseDoc, G, docOk ? CheckStatus.Pass : CheckStatus.Fail, docOk
            ? $"Response document shows {record.Role} on {record.EntraGroup} as assigned."
            : $"Response document shows {record.State}, {record.Role} on {record.EntraGroup}; expected Assigned, {scope.Role} on {scope.EntraGroup}."));

        checks.Add(Exact(VerifyCodes.RoleExact, "Reported and recorded", expected, record.AssignedRoles.Concat(report.AssignedRoles).Distinct(RoleComparer.Instance).ToList()));

        // v1.2: a Databricks row is tested live in Databricks (whoever set it up). Only the worker can read Databricks: with the
        // set-up switched on, the website's own step leaves the tests pending for it, so nothing completes untested.
        if (Databricks.DatabricksPolicy.ParseTarget(scope.RoleScope) is { } target && notConnected is null)
        {
            if (databricks is not null && databricks.Workspace(target.Workspace) is { } ws)
            {
                var decision = databricks.Policy.Decide(scope.EntraGroup);
                checks.AddRange(decision.ByAgent
                    ? await Databricks.DatabricksChecks.RunAsync(databricks.Access, ws, scope.EntraGroup, target, decision, ct)
                    : [new CheckResult(Databricks.DatabricksChecks.Workspace, G, CheckStatus.NotApplicable, $"Not tested in Databricks: {decision.Why}.")]);
                return new Verification(checks);
            }
            if (databricks is null && settings.AgentDatabricks && !settings.AllowUnverifiedEntra)
            {
                checks.Add(new CheckResult(Databricks.DatabricksChecks.Workspace, G, CheckStatus.Pending,
                    "Tested live in Databricks by the background agent within a few minutes."));
                return new Verification(checks);
            }
        }

        if (roles is null || directory is null || notConnected is not null)
        {
            checks.Add(new CheckResult(VerifyCodes.RoleInAzure, G, CheckStatus.NotApplicable, notConnected is not null
                ? $"Not read in Azure: {notConnected}. Verified against the response document and the team's report."
                : "No role assignment reader configured; verified against the response document and the team's report."));
            return new Verification(checks);
        }
        var groups = await directory.FindGroupsAsync(scope.EntraGroup, ct);
        if (groups.Count != 1)
        {
            checks.Add(new CheckResult(VerifyCodes.RoleInAzure, G, CheckStatus.Fail, $"Group {scope.EntraGroup} is not unique in Entra ({groups.Count} found)."));
            return new Verification(checks);
        }
        var actual = await AssignedAsync(groups[0].ObjectId, expected, ct);
        checks.Add(actual is null
            ? new CheckResult(VerifyCodes.RoleInAzure, G, CheckStatus.NotApplicable, "This scope type cannot be read; verified against the response document.")
            : Exact(VerifyCodes.RoleInAzure, "Assigned in Azure", expected, actual));
        return new Verification(checks);
    }

    /// <summary>The group's assignments at exactly the expected scopes; null if any scope cannot be read here.</summary>
    private async Task<IReadOnlyList<AssignedRole>?> AssignedAsync(string groupId, IReadOnlyList<AssignedRole> expected, CancellationToken ct)
    {
        var all = new List<AssignedRole>();
        foreach (var at in expected.Select(e => (e.Scope ?? "").Trim().TrimEnd('/')).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var found = await roles!.ListForPrincipalAsync(groupId, at.Length == 0 ? null : at, ct);
            if (found is null) return null;
            all.AddRange(found);
        }
        return all.Distinct(RoleComparer.Instance).ToList();
    }

    private static CheckResult Exact(string code, string what, IReadOnlyList<AssignedRole> expected, IReadOnlyList<AssignedRole> actual)
    {
        var missing = expected.Where(e => !actual.Contains(e, RoleComparer.Instance)).ToList();
        var extra = actual.Where(a => !expected.Contains(a, RoleComparer.Instance)).ToList();
        if (missing.Count == 0 && extra.Count == 0)
            return new CheckResult(code, CheckGroups.RoleTeam, CheckStatus.Pass, $"{what}: exactly {string.Join("; ", expected.Select(Describe))}.");
        var parts = new List<string>();
        if (missing.Count > 0) parts.Add($"missing {string.Join(", ", missing.Select(Describe))}");
        if (extra.Count > 0) parts.Add($"extra {string.Join(", ", extra.Select(Describe))}");
        return new CheckResult(code, CheckGroups.RoleTeam, CheckStatus.Fail, $"{what}: {string.Join("; ", parts)}. Correct the assignment or revoke the extra role.");
    }

    private static string Describe(AssignedRole a) => a.Scope is null ? a.Role : $"{a.Role} at {a.Scope}";

    /// <summary>Stage 6: login with the assigned access, permanent method registered, TAP deleted.</summary>
    /// <summary>
    /// Stage 7: the access is in place — the account exists, it is a member of the approved group and the group carries
    /// exactly the approved roles. Read once, right after the role step is verified; nothing here waits for the user.
    /// </summary>
    public async Task<Verification> FinalAsync(AccessRequest r, ReviewedScope scope, CancellationToken ct, string? notConnected = null)
    {
        const string G = CheckGroups.FinalVerification;
        if (directory is null || notConnected is not null)
            return new Verification([NoEntra(VerifyCodes.FinalAccess, G, notConnected)]);

        var user = await directory.FindUserAsync(r.DomainId!, ct);
        if (user is null)
            return new Verification([new CheckResult(VerifyCodes.FinalAccess, G, CheckStatus.Fail, $"No user {r.DomainId} in Entra.")]);

        var groups = await directory.FindGroupsAsync(scope.EntraGroup, ct);
        var member = groups.Count == 1 && await directory.IsMemberAsync(groups[0].ObjectId, user.ObjectId, ct);
        var access = member ? CheckStatus.Pass : CheckStatus.Fail;
        var detail = member ? $"{r.DomainId} is a member of {scope.EntraGroup}" : $"{r.DomainId} is not a member of {scope.EntraGroup}";
        // A Databricks row has no Azure role to compare: its Databricks access is tested live at the role step (DBX_* checks).
        if (member && roles is not null && !Databricks.DatabricksPolicy.IsDatabricksScope(scope.RoleScope))
        {
            var expected = WorkflowAgent.ExpectedRoles(scope);
            var actual = await AssignedAsync(groups[0].ObjectId, expected, ct);
            var exact = actual is not null && actual.Count == expected.Count && expected.All(e => actual.Contains(e, RoleComparer.Instance));
            if (actual is not null && !exact)
            {
                access = CheckStatus.Fail;
                detail += $"; group roles are {string.Join(", ", actual.Select(Describe))}, expected exactly {string.Join(", ", expected.Select(Describe))}";
            }
            else if (actual is not null) detail += $" with {string.Join(", ", expected.Select(Describe))}";
        }
        return new Verification([new CheckResult(VerifyCodes.FinalAccess, G, access, detail + ".")]);
    }

    private sealed class RoleComparer : IEqualityComparer<AssignedRole>
    {
        public static readonly RoleComparer Instance = new();

        public bool Equals(AssignedRole? x, AssignedRole? y) =>
            x is not null && y is not null && MappingSheet.Same(x.Role, y.Role)
            && string.Equals((x.Scope ?? "").Trim().TrimEnd('/'), (y.Scope ?? "").Trim().TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

        public int GetHashCode(AssignedRole obj) => MappingSheet.Normalize(obj.Role).ToUpperInvariant().GetHashCode();
    }
}
