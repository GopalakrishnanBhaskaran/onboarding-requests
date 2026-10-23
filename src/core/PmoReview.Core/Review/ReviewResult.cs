using PmoReview.Core.Domain;

namespace PmoReview.Core.Review;

public enum FindingSeverity { Info, Warning, Blocking }

public enum MappingStatus { Matched, NotFound, Ambiguous, NotApplicable }

/// <summary>An observation from the reviewer model (advisory). Host checks are in <see cref="ReviewResult.Checks"/>.</summary>
public sealed record Finding
{
    public required string Code { get; init; }
    public string? Field { get; init; }
    public required FindingSeverity Severity { get; init; }
    public required string Message { get; init; }
}

/// <summary>The single mapping-sheet row the request maps to (host-computed from a fresh read).</summary>
public sealed record MappingAssessment
{
    public required MappingStatus Status { get; init; }
    public string? RowId { get; init; }
    public int? RowVersion { get; init; }
    public string? EntraGroup { get; init; }
    public string? Role { get; init; }
    public string? RoleScope { get; init; }
    public GroupState? GroupState { get; init; }
    /// <summary>Entra cross-check result: the group exists (true), does not (false), or was not checked yet (null).</summary>
    public bool? GroupExistsInEntra { get; init; }
    public required string Explanation { get; init; }
}

/// <summary>Result contract v2. Mirrors <c>schema/review-result.v2.json</c>.</summary>
public sealed record ReviewResult
{
    public const string CurrentSchemaVersion = "2.0";

    public required string SchemaVersion { get; init; }
    public required string ReviewRunId { get; init; }
    public required int RequestRevision { get; init; }
    /// <summary>Host-computed: Fail if any check failed, Pending if any check has not run, else Pass.</summary>
    public required ReviewOutcome Outcome { get; init; }
    /// <summary>AI summary for approvers: what, why, group, role, risk flags, review results.</summary>
    public required string Summary { get; init; }
    public required IReadOnlyList<CheckResult> Checks { get; init; }
    public required IReadOnlyList<string> RiskFlags { get; init; }
    public required MappingAssessment Mapping { get; init; }
    public required IReadOnlyList<Finding> Findings { get; init; }
    /// <summary>Specific reasons sent back to the requester and PMO when the outcome is Fail.</summary>
    public required IReadOnlyList<string> ReturnReasons { get; init; }
    /// <summary>Always true. The review recommends; the Owner and Security decide.</summary>
    public required bool HumanApprovalRequired { get; init; }
    public required IReadOnlyList<string> Limitations { get; init; }
}
