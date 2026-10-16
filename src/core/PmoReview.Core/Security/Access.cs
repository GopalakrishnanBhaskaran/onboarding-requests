using PmoReview.Core.Common;
using PmoReview.Core.Domain;
using PmoReview.Core.Storage;

namespace PmoReview.Core.Security;

public enum Permission
{
    ViewRequests,
    EditRequests,
    AnalyzeRequests,
    ViewRuns,
    RecordDecision,
    ViewReferenceData,
    ManageReferenceData,
    RecoverRuns,
    /// <summary>Phase 2c: show a joiner's Temporary Access Pass once.</summary>
    RevealJoinerPass,
}

/// <summary>Role → permission table. Single place to change who can do what.</summary>
public static class AccessPolicy
{
    private static readonly Dictionary<AppRole, Permission[]> Table = new()
    {
        [AppRole.PMO] = [Permission.ViewRequests, Permission.EditRequests, Permission.AnalyzeRequests, Permission.ViewRuns, Permission.ViewReferenceData, Permission.RevealJoinerPass],
        [AppRole.Reviewer] = [Permission.ViewRequests, Permission.ViewRuns, Permission.RecordDecision, Permission.ViewReferenceData, Permission.RevealJoinerPass],
        [AppRole.Maintainer] = [Permission.ViewReferenceData, Permission.ManageReferenceData],
        [AppRole.Operator] = [Permission.ViewRuns, Permission.RecoverRuns],
    };

    public static bool Allows(IEnumerable<AppRole> roles, Permission permission) =>
        roles.Any(r => Table.TryGetValue(r, out var perms) && perms.Contains(permission));
}

/// <summary>Authenticated caller and the projects they hold roles in. Built server-side only.</summary>
public sealed class CallerContext(string userId, string displayName, IReadOnlyDictionary<string, IReadOnlySet<AppRole>> grants)
{
    public string UserId { get; } = userId;
    public string DisplayName { get; } = displayName;
    public IReadOnlyDictionary<string, IReadOnlySet<AppRole>> Grants { get; } = grants;

    /// <summary>
    /// Throws NotFound when the caller has no role in the project (does not reveal that it exists),
    /// Forbidden when they have a role but not this permission.
    /// </summary>
    public void Require(string projectId, Permission permission)
    {
        if (string.IsNullOrEmpty(projectId) || !Grants.TryGetValue(projectId, out var roles))
            throw AppException.NotFound("Project");
        if (!AccessPolicy.Allows(roles, permission))
            throw AppException.Forbidden();
    }

    public bool Has(string projectId, Permission permission) =>
        Grants.TryGetValue(projectId, out var roles) && AccessPolicy.Allows(roles, permission);

    public IEnumerable<string> ProjectsWith(Permission permission) =>
        Grants.Where(g => AccessPolicy.Allows(g.Value, permission)).Select(g => g.Key).OrderBy(p => p, StringComparer.Ordinal);
}

public sealed class AccessService(DataStores stores)
{
    /// <summary>Deny by default: a signed-in user with no userAccess rows gets an empty grant set.</summary>
    public async Task<CallerContext> ResolveAsync(ClientPrincipal principal, CancellationToken ct)
    {
        var rows = await stores.UserAccess.ListAsync(principal.UserId, ct);
        var grants = rows
            .Select(r => r.Value)
            .Where(r => r.UserId == principal.UserId && !string.IsNullOrEmpty(r.ProjectId))
            .GroupBy(r => r.ProjectId, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlySet<AppRole>)g.SelectMany(r => r.Roles).ToHashSet(),
                StringComparer.Ordinal);
        return new CallerContext(principal.UserId, principal.UserDetails, grants);
    }
}
