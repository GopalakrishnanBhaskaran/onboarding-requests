using PmoReview.Core.Common;
using PmoReview.Core.Domain;
using PmoReview.Core.Storage;

namespace PmoReview.Core.Security;

public enum Permission
{
    ViewRequests,
    SubmitRequests,
    ViewRuns,
    /// <summary>Owner stage. Counts only when the caller is also the Owner named on the request.</summary>
    ApproveAsOwner,
    ApproveAsSecurity,
    WorkIdTeamTasks,
    WorkRoleTeamTasks,
    ViewMappingSheet,
    /// <summary>PMO: add and change mapping rows.</summary>
    EditMappingSheet,
    /// <summary>ID team: record a created group on the mapping sheet (group name / state only).</summary>
    UpdateMappingGroup,
    ViewResponseDocument,
    ViewIssues,
    ViewReports,
    RecoverRuns,
}

/// <summary>Role → permission table. Single place to change who can do what. Nobody but Owner and Security approves.</summary>
public static class AccessPolicy
{
    private static readonly Permission[] Read = [Permission.ViewRequests, Permission.ViewRuns, Permission.ViewMappingSheet, Permission.ViewResponseDocument];

    private static readonly Dictionary<AppRole, Permission[]> Table = new()
    {
        [AppRole.Requester] = [.. Read, Permission.SubmitRequests],
        [AppRole.Owner] = [.. Read, Permission.ApproveAsOwner],
        [AppRole.Security] = [.. Read, Permission.ApproveAsSecurity, Permission.ViewIssues, Permission.ViewReports],
        [AppRole.IdTeam] = [.. Read, Permission.WorkIdTeamTasks, Permission.UpdateMappingGroup],
        [AppRole.RoleTeam] = [.. Read, Permission.WorkRoleTeamTasks],
        [AppRole.PMO] = [.. Read, Permission.EditMappingSheet, Permission.ViewReports],
        [AppRole.Operator] = [.. Read, Permission.ViewIssues, Permission.ViewReports, Permission.RecoverRuns],
        // Legacy v1 roles grant nothing.
        [AppRole.Reviewer] = [],
        [AppRole.Maintainer] = [],
    };

    public static bool Allows(IEnumerable<AppRole> roles, Permission permission) =>
        roles.Any(r => Table.TryGetValue(r, out var perms) && perms.Contains(permission));
}

/// <summary>Authenticated caller and the projects they hold roles in. Built server-side only.</summary>
public sealed class CallerContext(string userId, string displayName, IReadOnlyDictionary<string, IReadOnlySet<AppRole>> grants)
{
    public string UserId { get; } = userId;
    /// <summary>Sign-in name from the platform principal (userDetails). Used to match the named Owner.</summary>
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

    public bool InProject(string projectId) => Grants.ContainsKey(projectId);

    public bool Has(string projectId, Permission permission) =>
        Grants.TryGetValue(projectId, out var roles) && AccessPolicy.Allows(roles, permission);

    public IEnumerable<string> ProjectsWith(Permission permission) =>
        Grants.Where(g => AccessPolicy.Allows(g.Value, permission)).Select(g => g.Key).OrderBy(p => p, StringComparer.Ordinal);

    /// <summary>
    /// The caller is the person named on the request (sign-in name, case-insensitive). A guest's sign-in can reach the
    /// app as its mail address (owner@gmail.com), as its guest UPN (owner_gmail.com#EXT#@tenant.onmicrosoft.com) or with a
    /// provider prefix (live.com#owner@gmail.com); all three name the same person.
    /// </summary>
    public bool Is(string? signInName) =>
        !string.IsNullOrWhiteSpace(signInName) && !string.IsNullOrWhiteSpace(DisplayName) && SignInKey(DisplayName) == SignInKey(signInName);

    public static string SignInKey(string signIn)
    {
        var t = signIn.Trim().ToLowerInvariant();
        var hash = t.IndexOf('#');
        if (hash > 0 && t.IndexOf("#ext#@", StringComparison.Ordinal) is var ext and > 0)
        {
            var local = t[..ext];
            var underscore = local.LastIndexOf('_');
            if (underscore > 0) return $"{local[..underscore]}@{local[(underscore + 1)..]}";
        }
        else if (hash > 0 && t[..hash] is "live.com" or "gmail.com" or "mail" or "sts" && t[(hash + 1)..].Contains('@'))
        {
            return t[(hash + 1)..];
        }
        return t;
    }
}

public sealed class AccessService(DataStores stores)
{
    /// <summary>userAccess rows registered by sign-in name carry this user id prefix (the platform's own ids never do).</summary>
    public const string SignInPrefix = "signin:";

    /// <summary>The userAccess user id of a registration by sign-in name: same normalisation as the named-Owner match.</summary>
    public static string SignInUserId(string signIn) => SignInPrefix + CallerContext.SignInKey(signIn);

    /// <summary>
    /// Deny by default: a signed-in user with no userAccess rows gets an empty grant set. Rows match the platform user id,
    /// or the principal's sign-in name (<see cref="SignInUserId"/>); the roles of both add up.
    /// </summary>
    public async Task<CallerContext> ResolveAsync(ClientPrincipal principal, CancellationToken ct)
    {
        // A platform user id never starts with the prefix; one that does could reach a sign-in registration: deny.
        if (principal.UserId.StartsWith(SignInPrefix, StringComparison.OrdinalIgnoreCase))
            return new CallerContext(principal.UserId, principal.UserDetails, new Dictionary<string, IReadOnlySet<AppRole>>());
        var keys = new List<string> { principal.UserId };
        if (!string.IsNullOrWhiteSpace(principal.UserDetails)) keys.Add(SignInUserId(principal.UserDetails));
        var rows = new List<UserAccess>();
        foreach (var key in keys) rows.AddRange((await stores.UserAccess.ListAsync(key, ct)).Select(r => r.Value));
        var grants = rows
            .Where(r => keys.Contains(r.UserId, StringComparer.Ordinal) && !string.IsNullOrEmpty(r.ProjectId))
            .GroupBy(r => r.ProjectId, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlySet<AppRole>)g.SelectMany(r => r.Roles).ToHashSet(),
                StringComparer.Ordinal);
        return new CallerContext(principal.UserId, principal.UserDetails, grants);
    }
}
