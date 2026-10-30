namespace PmoReview.Core.Directory;

/// <summary>
/// One group the user is in (F5 plan). <see cref="Direct"/> = the user is a member of the group itself (removable);
/// otherwise the membership comes through another group and goes with it. <see cref="NotChangeable"/> = why the worker
/// must not try to remove it (dynamic membership, role-assignable group, not a group at all; F102) — a leftover for people.
/// <see cref="DirectoryRole"/> = it gives the user an Entra directory role (a directory role id, or a role-assignable group):
/// the AI agent then changes nothing for this account; people offboard it (PERMISSIONS.md row C).
/// </summary>
public sealed record GroupMembership(string GroupObjectId, string DisplayName, bool Direct, string? NotChangeable = null, bool DirectoryRole = false);

/// <summary>One Azure role assignment of the user (F82): its full id (<c>/…/providers/Microsoft.Authorization/roleAssignments/{name}</c>).</summary>
public sealed record RoleAssignmentRef(string Id, string Scope, string RoleDefinitionId, string PrincipalId, string PrincipalType);

/// <summary>
/// F5 offboarding reads (worker, held read permissions only; F77, F79, F94, F102). Never a write.
/// </summary>
public interface IOffboardingReader
{
    /// <summary>Transitive groups (and anything else getMemberGroups returns), each with whether the user is a direct member.</summary>
    Task<IReadOnlyList<GroupMembership>> MembershipsAsync(string userObjectId, CancellationToken ct);
    /// <summary>Ids of the user's Temporary Access Pass methods (from the authentication methods list, F79).</summary>
    Task<IReadOnlyList<string>> TapMethodIdsAsync(string userObjectId, CancellationToken ct);
}

/// <summary>
/// F5 offboarding writes in Entra (v1.1, Gokul's written approval 30 Oct 2026; worker only, behind <c>AGENT_OFFBOARDING</c>).
/// Exactly these calls (F71–F75): revoke sign-in sessions, delete a TAP method, remove a direct membership (<c>/$ref</c>
/// only), disable the account, and — only with <c>OFFBOARD_DELETE_USER</c> — soft-delete it. Each one is idempotent: an
/// object already gone counts as done.
/// </summary>
public interface IOffboardingWriter
{
    Task RevokeSessionsAsync(string userObjectId, CancellationToken ct);
    Task DeleteTapAsync(string userObjectId, string methodId, CancellationToken ct);
    Task RemoveMemberAsync(string groupObjectId, string userObjectId, CancellationToken ct);
    Task DisableAsync(string userObjectId, CancellationToken ct);
    Task DeleteUserAsync(string userObjectId, CancellationToken ct);
}

/// <summary>F5 Azure RBAC reads (worker; Reader at each subscription in scope, F82, F84). Never a write.</summary>
public interface IRoleAssignmentLister
{
    /// <summary>Subscriptions the worker identity can see (F84).</summary>
    Task<IReadOnlyList<string>> SubscriptionsAsync(CancellationToken ct);
    /// <summary>The user's assignments at, above or below the subscription; null = the worker may not read there.</summary>
    Task<IReadOnlyList<RoleAssignmentRef>?> AssignmentsInSubscriptionAsync(string subscriptionId, string principalObjectId, CancellationToken ct);
}

/// <summary>
/// F5 Azure RBAC write (worker only, behind <c>AGENT_OFFBOARDING</c>): deletes one role assignment of the user being
/// offboarded (F83). The worker holds Role Based Access Control Administrator with a users-only delete condition; the
/// code also reads the assignment first and refuses one whose principal is anyone else.
/// </summary>
public interface IRoleAssignmentRemover
{
    /// <summary>Deletes one assignment of <paramref name="principalObjectId"/> (read first; gone = done; another principal = refused).</summary>
    Task DeleteAsync(RoleAssignmentRef assignment, string principalObjectId, CancellationToken ct);
}
