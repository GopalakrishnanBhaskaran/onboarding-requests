using PmoReview.Core.Domain;

namespace PmoReview.Core.Review;

/// <summary>Provider identity recorded on every run.</summary>
public sealed record ReviewerInfo(string Provider, string Version, string? PromptVersion);

/// <summary>Hard limits for a model review. The time budget must stay below the SWA 45 s gateway limit (VERIFIED_FACTS F3).</summary>
public sealed record ReviewLimits
{
    public TimeSpan TimeBudget { get; init; } = TimeSpan.FromSeconds(30);
    public int MaxInputChars { get; init; } = 24_000;
    public int MaxOutputTokens { get; init; } = 1_500;
    /// <summary>Retries after HTTP 429 (with backoff, inside the time budget).</summary>
    public int MaxThrottleRetries { get; init; } = 3;
}

/// <summary>
/// Everything a reviewer model may see. Request text is untrusted data; records are already masked. The host has read
/// both sources of truth fresh and computed every check; the model can add concerns, never remove a failed check.
/// </summary>
public sealed record ReviewContext(
    RequestSnapshot Snapshot,
    string ReviewRunId,
    IReadOnlyList<CheckResult> HostChecks,
    IReadOnlyList<string> RiskFlags,
    MappingAssessment Mapping,
    IReadOnlyList<ResponseRecord> UserRecords);

/// <summary>
/// What a reviewer proposes. There is deliberately no outcome or approval field: the host computes the outcome.
/// <see cref="Recommendation"/> "Fail" adds a failing AI check; nothing the model says can make a review pass.
/// </summary>
public sealed record ModelReview
{
    public required string Summary { get; init; }
    public IReadOnlyList<Finding> Findings { get; init; } = [];
    /// <summary>"Pass" or "Fail". Advisory.</summary>
    public string? Recommendation { get; init; }
    public IReadOnlyList<string> Limitations { get; init; } = [];
    /// <summary>True when an AI model produced this (adds the AI review check).</summary>
    public bool UsedModel { get; init; }
    public TokenUsage? Usage { get; init; }
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

/// <summary>Deterministic review policy. Defaults are the pilot's; a project can set its own group naming pattern.</summary>
public sealed record ReviewPolicy
{
    /// <summary>Upper-case, dash-separated names (e.g. GRP-SALES-DBW-READ). Used when the project sets none.</summary>
    public string DefaultGroupNamePattern { get; init; } = "^[A-Z][A-Z0-9]*(-[A-Z0-9]+)+$";
    /// <summary>Minimum justification length by access level rank (1 read, 2 write/use, 3 admin).</summary>
    public IReadOnlyList<int> MinJustificationByRank { get; init; } = [20, 40, 80];
    /// <summary>Active assignments at or above this count raise the "existing broad access" flag.</summary>
    public int BroadAccessThreshold { get; init; } = 3;
    public IReadOnlyList<string> PrivilegedWords { get; init; } =
        ["owner", "admin", "administrator", "contributor", "user access administrator", "global", "privileged", "elevated", "full control", "manage"];
    public IReadOnlyList<string> ReadWords { get; init; } = ["read", "reader", "viewer", "view", "browse", "readonly", "read-only", "consumer"];
}
