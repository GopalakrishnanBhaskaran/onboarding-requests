using PmoReview.Core.Common;
using PmoReview.Core.Domain;

namespace PmoReview.Core.Directory;

/// <param name="Mail">The user's SMTP address in Entra (<c>mail</c>), if any; the F2 TAP recipient check compares it (F95).</param>
public sealed record DirectoryUser(string ObjectId, string UserPrincipalName, string? UserType, bool AccountEnabled, string? Mail = null)
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
public class DirectoryReadException(string category, Exception? inner = null) : Exception(category, inner)
{
    public string Category { get; } = category;
}

/// <summary>Directory or role-assignment write failure (category only). <c>…Forbidden</c> = the permission is not granted.</summary>
public sealed class DirectoryWriteException(string category, Exception? inner = null) : DirectoryReadException(category, inner)
{
    public bool Forbidden => Category.EndsWith("Forbidden", StringComparison.Ordinal);
}

/// <summary>
/// A new member account. The initial password (<see cref="Secret"/>) is random, sent to Entra and — only with the agent
/// TAP on — mailed once to the joiner in the same agent run; it is never stored or logged.
/// </summary>
public sealed record NewUser(string UserPrincipalName, string DisplayName, string MailNickname, string? GivenName, string? Surname);

/// <summary>
/// A Temporary Access Pass the agent just created (agent TAP, off until approved). <see cref="Pass"/> is a
/// <see cref="Secret"/>: it goes into one mail to the joiner and nowhere else; the app records only the times and the
/// one-time flag.
/// </summary>
public sealed record IssuedTap(Secret Pass, DateTimeOffset IssuedAt, DateTimeOffset ExpiresAt, bool OneTime);

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
/// Agent provisioning (v2.1, worker only, only after the Owner and Security approvals): creates the user, the security
/// group and the membership, and — with the agent TAP on — one one-time Temporary Access Pass for a user it has just
/// created. Create calls only — no update, delete or licence call. Every call is preceded by a read, so a repeat finds the
/// object instead of creating it again.
/// </summary>
public interface IDirectoryWriter
{
    Task<DirectoryUser> CreateUserAsync(NewUser user, Secret initialPassword, CancellationToken ct);
    /// <summary>Security group (not mail-enabled) owned by the agent's own identity (Group.Create: no owner = unmodifiable group).</summary>
    Task<DirectoryGroup> CreateSecurityGroupAsync(string displayName, string description, CancellationToken ct);
    /// <summary>Adds the user to the group; an existing membership counts as done.</summary>
    Task AddMemberAsync(string groupObjectId, string userObjectId, CancellationToken ct);
    /// <summary>
    /// True when the agent's current token carries the TAP write permission (UserAuthMethod-TAP.ReadWrite.All). Checked
    /// before a user is created, so the password and the pass go out together (a new permission can take ~24 h, F61).
    /// </summary>
    Task<bool> CanIssueTapAsync(CancellationToken ct);
    /// <summary>One-time Temporary Access Pass for the user (agent TAP only; Graph's TAP create call, F66).</summary>
    Task<IssuedTap> CreateTemporaryAccessPassAsync(string userObjectId, int lifetimeMinutes, CancellationToken ct);
}

/// <summary>
/// Agent role assignment (v2.1, worker only, after both approvals): assigns one Azure role definition at one scope to a
/// group. Idempotent (deterministic assignment name; an existing assignment counts as done). The worker identity holds
/// Role Based Access Control Administrator constrained to the allowed roles and user/group principals (VERIFIED_FACTS F60).
/// </summary>
public interface IRoleAssignmentWriter
{
    Task AssignAsync(string principalObjectId, string principalType, string roleDefinitionId, string scope, CancellationToken ct);
}

/// <summary>
/// Read-only view of role assignments (e.g. Azure RBAC) for Stage 5 verification. Returns null when the scope type
/// cannot be read by this implementation; the agent then verifies against the response document only and says so.
/// </summary>
public interface IRoleAssignmentReader
{
    Task<IReadOnlyList<AssignedRole>?> ListForPrincipalAsync(string principalObjectId, string? scope, CancellationToken ct);
}
