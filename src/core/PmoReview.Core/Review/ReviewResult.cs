using PmoReview.Core.Domain;

namespace PmoReview.Core.Review;

public enum FindingSeverity { Info, Warning, Blocking }

public enum MappingStatus { Matched, NotFound, Ambiguous, NotApplicable }

public sealed record Finding
{
    public required string Code { get; init; }
    public string? Field { get; init; }
    public required FindingSeverity Severity { get; init; }
    public required string Message { get; init; }
    public string? RuleId { get; init; }
    public int? RuleVersion { get; init; }
    public string? SourceReference { get; init; }
}

public sealed record MappingAssessment
{
    public required MappingStatus Status { get; init; }
    public string? MappingId { get; init; }
    public int? MappingVersion { get; init; }
    public required string Explanation { get; init; }
}

/// <summary>Result contract v1. Mirrors <c>schema/review-result.v1.json</c>.</summary>
public sealed record ReviewResult
{
    public const string CurrentSchemaVersion = "1.0";

    public required string SchemaVersion { get; init; }
    public required string ReviewRunId { get; init; }
    public required int RequestRevision { get; init; }
    public required Readiness Readiness { get; init; }
    public required string Summary { get; init; }
    public required IReadOnlyList<Finding> Findings { get; init; }
    public required MappingAssessment MappingAssessment { get; init; }
    public required IReadOnlyList<string> ClarificationQuestions { get; init; }
    /// <summary>Always true. A review never replaces a human decision.</summary>
    public required bool HumanReviewRequired { get; init; }
    public required IReadOnlyList<string> Limitations { get; init; }
}
