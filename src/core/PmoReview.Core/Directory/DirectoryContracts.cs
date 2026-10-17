using PmoReview.Core.Domain;

namespace PmoReview.Core.Directory;

public sealed record DirectoryUser(string ObjectId, string UserPrincipalName, string? UserType, bool AccountEnabled)
{
    public bool IsGuest => string.Equals(UserType, "Guest", StringComparison.OrdinalIgnoreCase);
}

public sealed record DirectoryGroup(string ObjectId, string DisplayName, bool SecurityEnabled);

/// <summary>
/// Temporary Access Pass method metadata as Entra returns it on read. Entra never returns the pass itself on a GET;
/// this app has no field for it.
/// </summary>
public sealed record TapMethod(DateTimeOffset? CreatedAt, DateTimeOffset? StartAt, int? LifetimeMinutes, bool? IsUsableOnce, bool? IsUsable, string? UsabilityReason);

/// <summary>Authentication methods registered for a user: method type names, plus the TAP if one is still present.</summary>
public sealed record AuthMethods(IReadOnlyList<string> MethodTypes, TapMethod? Tap)
{
    /// <summary>Method types that are neither a password nor a Temporary Access Pass: a permanent method the user registered.</summary>
    public static readonly IReadOnlySet<string> NotPermanent = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "password", "temporaryAccessPass" };

    public bool HasPermanentMethod => MethodTypes.Any(m => !NotPermanent.Contains(m));
}

public sealed record SignInSummary(DateTimeOffset At, bool Success);

/// <summary>Directory read failure, recorded as a category (never details). Retried by the agent, at most 3 times per step.</summary>
public sealed class DirectoryReadException(string category, Exception? inner = null) : Exception(category, inner)
{
    public string Category { get; } = category;
}

/// <summary>
/// Read-only view of Microsoft Entra for the AI agent: Stage 2 cross-check, Stage 4 and 6 verification, escalation.
/// The implementation holds read permissions only and has no code path that writes to Entra.
/// </summary>
public interface IDirectoryReader
{
    Task<DirectoryUser?> FindUserAsync(string userPrincipalName, CancellationToken ct);
    /// <summary>Every group with exactly this display name (more than one = ambiguous).</summary>
    Task<IReadOnlyList<DirectoryGroup>> FindGroupsAsync(string displayName, CancellationToken ct);
    Task<bool> IsMemberAsync(string groupObjectId, string userObjectId, CancellationToken ct);
    Task<AuthMethods> GetAuthMethodsAsync(string userObjectId, CancellationToken ct);
    /// <summary>Most recent successful sign-in since the given time, or null.</summary>
    Task<SignInSummary?> LastSuccessfulSignInAsync(string userObjectId, DateTimeOffset since, CancellationToken ct);
    /// <summary>Sign-in name or mail of the user's manager, or null.</summary>
    Task<string?> GetManagerAsync(string userPrincipalName, CancellationToken ct);
}

/// <summary>
/// Read-only view of role assignments (e.g. Azure RBAC) for Stage 5 verification. Returns null when the scope type
/// cannot be read by this implementation; the agent then verifies against the response document only and says so.
/// </summary>
public interface IRoleAssignmentReader
{
    Task<IReadOnlyList<AssignedRole>?> ListForPrincipalAsync(string principalObjectId, string? scope, CancellationToken ct);
}
