using PmoReview.Core.Domain;
using PmoReview.Core.Review;

namespace PmoReview.Adapters.Review;

/// <summary>
/// ACTIVE pilot reviewer. Deterministic, no network calls and no cost. The host checks are the review; this adds a
/// short summary and states its limitation. It never adds an AI check.
/// </summary>
public sealed class RuleOnlyReviewModel : IReviewModel
{
    public const string ProviderName = "RuleOnly";

    public ReviewerInfo Info { get; } = new(ProviderName, "2.0.0", PromptVersion: null);

    public Task<ModelReview> ReviewAsync(ReviewContext ctx, ReviewLimits limits, CancellationToken ct)
    {
        var failed = ctx.HostChecks.Count(c => c.Status == CheckStatus.Fail);
        var pending = ctx.HostChecks.Count(c => c.Status == CheckStatus.Pending);
        var summary = failed > 0
            ? $"Rule-only review of revision {ctx.Snapshot.Revision}: {failed} check(s) failed."
            : pending > 0
                ? $"Rule-only review of revision {ctx.Snapshot.Revision}: no failures so far; {pending} check(s) pending."
                : $"Rule-only review of revision {ctx.Snapshot.Revision}: all checks passed.";
        return Task.FromResult(new ModelReview
        {
            Summary = summary,
            Limitations = ["Rule-only reviewer: no AI model was used. Justification consistency was assessed by length and keyword rules only."],
        });
    }
}
