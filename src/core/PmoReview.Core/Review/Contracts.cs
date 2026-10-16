using PmoReview.Core.Domain;

namespace PmoReview.Core.Review;

/// <summary>Provider identity recorded on every run.</summary>
public sealed record ReviewerInfo(string Provider, string Version, string? PromptVersion);

/// <summary>Hard limits for a model review. The time budget must stay below the SWA 45 s gateway limit (VERIFIED_FACTS F3).</summary>
public sealed record ReviewLimits
{
    public int MaxTurns { get; init; } = 4;
    public int MaxToolCalls { get; init; } = 4;
    public TimeSpan TimeBudget { get; init; } = TimeSpan.FromSeconds(30);
    public int MaxInputChars { get; init; } = 24_000;
    public int MaxOutputTokens { get; init; } = 1_500;
}

/// <summary>Read-only reference lookups, bound by the host to the snapshot's project.</summary>
public interface IReviewToolset
{
    Task<IReadOnlyList<RequirementRule>> GetPublishedRequirementsAsync(string projectId, string requestType, DateOnly effectiveDate, CancellationToken ct);

    Task<IReadOnlyList<RoleMapping>> GetApprovedRoleMappingAsync(string projectId, string businessRole, string environment, DateOnly effectiveDate, CancellationToken ct);
}

/// <summary>Everything a reviewer may see. Request text inside the snapshot is untrusted data.</summary>
public sealed record ReviewContext(
    RequestSnapshot Snapshot,
    string ReviewRunId,
    DateOnly EffectiveDate,
    IReadOnlyList<Finding> DeterministicFindings,
    IReviewToolset Tools);

/// <summary>
/// What a reviewer proposes. There is deliberately no readiness or approval field:
/// the host computes readiness and validates every citation.
/// </summary>
public sealed record ModelReview
{
    public required string Summary { get; init; }
    public IReadOnlyList<Finding> Findings { get; init; } = [];
    public MappingAssessment? MappingAssessment { get; init; }
    public IReadOnlyList<string> ClarificationQuestions { get; init; } = [];
    public IReadOnlyList<string> Limitations { get; init; } = [];
}

public interface IReviewModel
{
    ReviewerInfo Info { get; }

    Task<ModelReview> ReviewAsync(ReviewContext ctx, ReviewLimits limits, CancellationToken ct);
}

/// <summary>Review could not produce a trustworthy result. The run is persisted as Failed with this category.</summary>
public sealed class ReviewFailedException(ErrorCategory category, string message, Exception? inner = null) : Exception(message, inner)
{
    public ErrorCategory Category { get; } = category;
}
