namespace PmoReview.Core.Domain;

/// <summary>
/// Who does what (strict). The AI agent is not a role: it reviews, validates, summarises, routes, tracks, verifies and
/// reports, and never approves. Only with v2.1 provisioning switched on (approved) and only after both approvals does the
/// worker create the user, group and membership and assign allow-listed Azure roles; the TAP stays with the ID team.
/// </summary>
public enum AppRole
{
    /// <summary>Submits onboarding requests.</summary>
    Requester,
    /// <summary>Approves the business need. Counts only on requests that name this person as Owner.</summary>
    Owner,
    /// <summary>Approves risk and access level (Stage 3, after the Owner).</summary>
    Security,
    /// <summary>Creates the user ID and Entra groups, adds the membership, issues the Temporary Access Pass.</summary>
    IdTeam,
    /// <summary>Assigns roles to the groups/users and updates the response document.</summary>
    RoleTeam,
    /// <summary>Source of the mapping sheet. Informed only; never approves.</summary>
    PMO,
    /// <summary>Recovers stuck reviews, reads issues and reports.</summary>
    Operator,
    /// <summary>Legacy v1 role. Kept so old userAccess rows still load; grants nothing in v2.</summary>
    Reviewer,
    /// <summary>Legacy v1 role. Kept so old userAccess rows still load; grants nothing in v2.</summary>
    Maintainer,
    /// <summary>
    /// v1.1 (F3): decides Prod escalations — any level above Read in a Prod environment — after the Owner and Security
    /// approvals. Never the requester, the Owner approver or the Security approver of the same request.
    /// </summary>
    ProdApprover,
    /// <summary>
    /// v1.1 (F4): the Data Steward uploads the PMO mapping sheet and the people inventory, sees diffs, rolls back, sees mapping
    /// quality issues. Named DataMaintainer until 31 Oct 2026 (Gokul).
    /// </summary>
    DataSteward,
    /// <summary>Earlier name of <see cref="DataSteward"/> (until 31 Oct 2026). Kept so rows saved under it still load; read as DataSteward.</summary>
    DataMaintainer,
}

/// <summary>I1 ResourceGroupType values and the F1 "All" option.</summary>
public static class ResourceGroupTypes
{
    public const string Domain = "Domain";
    public const string Ph = "PH";
    public const string Acq = "ACQ";
    public const string All = "All Domains + PH + ACQ";
    public static readonly IReadOnlyList<string> Values = [Domain, Ph, Acq];

    public static string? Canonical(string? value) => Values.FirstOrDefault(v => string.Equals(v, value?.Trim(), StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// I1 access levels, lowest first (F3: in Prod only <see cref="Read"/> is fulfilled automatically). Mapping rows imported
/// before v1.1 carry job roles (Data Engineer, Support Reader …): they rank as Read only when every Azure role they carry
/// is read-only (decision 9, 30 Oct 2026); otherwise above Read.
/// </summary>
public static class AccessLevels
{
    public const string Read = "Read";
    public static readonly IReadOnlyList<string> Ordered = [Read, "Write", "Contributor", "Owner", "Admin"];

    /// <summary>Built-in Azure roles that only read (Microsoft Learn built-in roles, F62).</summary>
    public static readonly IReadOnlySet<string> ReadOnlyRoleIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "acdd72a7-3385-48ef-bd42-f606fba81ae7", // Reader
        "2a2b9908-6ea1-4ae2-8e65-a410df84e7d1", // Storage Blob Data Reader
        "21090545-7ca7-4776-b22c-e363652d74d2", // Key Vault Reader
        "4633458b-17de-408a-b874-0445c86b69e6", // Key Vault Secrets User
    };

    public static string? Canonical(string? level) => Ordered.FirstOrDefault(l => string.Equals(l, level?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>0 = Read … 4 = Admin; a level outside the I1 enum = -1.</summary>
    public static int Rank(string? level) => Canonical(level) is { } l ? Ordered.ToList().IndexOf(l) : -1;
}

public enum ProcessingStatus { Queued, Running, Completed, Failed, Superseded }

/// <summary>Stage 2 outcome. Pending = a check (the Entra cross-check) has not run yet; never sent for approval.</summary>
public enum ReviewOutcome { Pass, Fail, Pending }

public enum ErrorCategory { Timeout, MalformedResult, Conflict, DependencyUnavailable, Internal }

/// <summary>One check result. Flagged = a recorded risk flag; it is shown to approvers and never fails the review.</summary>
public enum CheckStatus { Pass, Fail, Pending, NotApplicable, Flagged }

/// <summary>Whether the mapping sheet says the Entra group exists or is to be created by the ID team.</summary>
public enum GroupState { Exists, ToBeCreated }
