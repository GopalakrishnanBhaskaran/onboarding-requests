using System.Security.Cryptography;
using System.Text;
using PmoReview.Core.Domain;
using PmoReview.Core.Workflow;

namespace PmoReview.Core.Resources;

/// <summary>
/// F3 (Gokul, 30 Oct 2026): Production is read-only by default. In an environment tagged Prod the only level fulfilled
/// without a person is Read; anything above Read needs an Approved ProdWrite escalation from a ProdApprover, bound to the
/// request revision and the exact scope the approvers saw. Every grant point asks this class: the selector (F1), the
/// Security approval (flow gate), the agent before provisioning or assigning roles, and the team reports.
/// </summary>
public static class ProdGuard
{
    /// <summary>
    /// Why this access is above Read in Prod, or null when it is not Prod or it is Read. "Read" means: the I1 level Read
    /// (or a job-role level) AND every Azure role read-only AND not privileged. A row labelled Read that carries a writing
    /// role is above Read: the label never outranks the roles.
    /// </summary>
    public static string? AboveReadInProd(string? environment, string? accessLevel, IReadOnlyList<AzureRoleGrant> grants, bool privileged)
    {
        if (!Environments.IsProduction(environment)) return null;
        var rank = AccessLevels.Rank(accessLevel);
        if (rank > 0) return $"access level {accessLevel} is above Read in Prod";
        if (privileged) return "the mapping row is flagged privileged";
        var writing = grants.Where(g => g.RoleId is null || !AccessLevels.ReadOnlyRoleIds.Contains(g.RoleId)).Select(g => g.RoleName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (writing.Count > 0) return $"Azure role(s) {string.Join(", ", writing)} are not read-only";
        if (rank < 0 && grants.Count == 0)
            return $"access level {accessLevel} is not the Read level and the row has no read-only Azure role to show it only reads";
        return null;
    }

    public static string? AboveReadInProd(MappingRow row, string? environment) =>
        AboveReadInProd(Effective(row, environment ?? row.Environment), row.AccessLevel, row.Grants(), row.Privileged);

    /// <summary>
    /// For a reviewed request: the scope the approvers saw, the request's access level and its mapping row (a row that is
    /// gone counts as privileged).
    /// </summary>
    public static string? AboveReadInProd(AccessRequest r, ReviewedScope scope, MappingRow? row) =>
        AboveReadInProd(Effective(row, scope.Environment ?? r.Environment), r.AccessLevel, scope.Grants(), Privileged(scope, row));

    /// <summary>
    /// The environment the guard judges. A row without an environment applies to every environment, Prod included: when an
    /// environment is named at all, such a row is guarded as Prod — choosing Dev for the same group never avoids the
    /// ProdApprover (review finding 4, 30 Oct 2026).
    /// </summary>
    public static string? Effective(MappingRow? row, string? environment) =>
        row is not null && string.IsNullOrWhiteSpace(row.Environment) && !string.IsNullOrWhiteSpace(environment) ? "Prod" : environment;

    /// <summary>Privileged: the row says so, or the review flagged it (a row that is gone counts as privileged: the safe side).</summary>
    public static bool Privileged(ReviewedScope scope, MappingRow? row) =>
        row is null || row.Privileged || scope.RiskFlags.Contains(RiskFlags.Privileged);

    /// <summary>What a ProdWrite decision is bound to: group, role, level, environment and every Azure role.</summary>
    public static string Fingerprint(AccessRequest r, ReviewedScope scope)
    {
        var grants = scope.Grants().Select(g => $"{g.RoleId ?? g.RoleName.Trim().ToLowerInvariant()}@{g.Where.Trim().TrimEnd('/').ToLowerInvariant()}").Order(StringComparer.Ordinal);
        var text = string.Join("|", scope.EntraGroup.Trim().ToLowerInvariant(), scope.Role.Trim().ToLowerInvariant(), (r.AccessLevel ?? "").Trim().ToLowerInvariant(),
            (scope.Environment ?? r.Environment ?? "").Trim().ToLowerInvariant(), string.Join(",", grants));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..32];
    }

    /// <summary>The escalation id of a request revision's ProdWrite decision (one per revision and scope).</summary>
    public static string EscalationId(AccessRequest r, ReviewedScope scope) =>
        Escalation.MakeId(EscalationKind.ProdWrite, $"{r.ProjectId}|{r.Id}|r{r.Revision}|{Fingerprint(r, scope)}");

    /// <summary>
    /// The one check every grant point runs. True when the access may be fulfilled: not Prod-above-Read, or a ProdWrite
    /// escalation for this request, this revision and this exact scope is Approved.
    /// </summary>
    public static bool MayFulfil(AccessRequest r, ReviewedScope scope, MappingRow? row, Escalation? escalation, out string? reason)
    {
        reason = AboveReadInProd(r, scope, row);
        if (reason is null) return true;
        return escalation is { Kind: EscalationKind.ProdWrite, Status: EscalationStatus.Approved } e
               && e.ProjectId == r.ProjectId && e.RequestId == r.Id && e.RequestRevision == r.Revision && e.ScopeFingerprint == Fingerprint(r, scope);
    }
}
