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
}

public enum ProcessingStatus { Queued, Running, Completed, Failed, Superseded }

/// <summary>Stage 2 outcome. Pending = a check (the Entra cross-check) has not run yet; never sent for approval.</summary>
public enum ReviewOutcome { Pass, Fail, Pending }

public enum ErrorCategory { Timeout, MalformedResult, Conflict, DependencyUnavailable, Internal }

/// <summary>One check result. Flagged = a recorded risk flag; it is shown to approvers and never fails the review.</summary>
public enum CheckStatus { Pass, Fail, Pending, NotApplicable, Flagged }

/// <summary>Whether the mapping sheet says the Entra group exists or is to be created by the ID team.</summary>
public enum GroupState { Exists, ToBeCreated }
