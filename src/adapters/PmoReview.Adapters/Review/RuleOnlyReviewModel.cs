using PmoReview.Core.Review;

namespace PmoReview.Adapters.Review;

/// <summary>
/// ACTIVE pilot reviewer. Deterministic, template-based, no network calls and no cost.
/// Builds the mapping assessment, clarification questions and summary from the host's findings.
/// </summary>
public sealed class RuleOnlyReviewModel : IReviewModel
{
    public const string ProviderName = "RuleOnly";

    public ReviewerInfo Info { get; } = new(ProviderName, "1.0.0", PromptVersion: null);

    public async Task<ModelReview> ReviewAsync(ReviewContext ctx, ReviewLimits limits, CancellationToken ct)
    {
        var s = ctx.Snapshot;
        MappingAssessment? mapping = null;
        if (!string.IsNullOrWhiteSpace(s.Role) && !string.IsNullOrWhiteSpace(s.Environment)
            && ctx.DeterministicFindings.All(f => f.Code != FindingCodes.UnsupportedProject))
        {
            var matches = await ctx.Tools.GetApprovedRoleMappingAsync(s.ProjectId, s.Role, s.Environment, ctx.EffectiveDate, ct);
            mapping = matches.Count switch
            {
                0 => new MappingAssessment { Status = MappingStatus.NotFound, Explanation = "No published mapping matches the requested business role and environment." },
                1 => new MappingAssessment
                {
                    Status = MappingStatus.Matched, MappingId = matches[0].MappingId, MappingVersion = matches[0].Version,
                    Explanation = $"Role and environment match published mapping {matches[0].MappingId} v{matches[0].Version} (reference group {matches[0].GroupReference}; source {matches[0].SourceReference}). Reference only; nothing is assigned.",
                },
                _ => new MappingAssessment { Status = MappingStatus.Ambiguous, Explanation = $"{matches.Count} published mappings match; a human must choose." },
            };
        }

        var findings = new List<Finding>();
        if (mapping?.Status == MappingStatus.Matched && string.Equals(s.Environment, "Prod", StringComparison.OrdinalIgnoreCase))
        {
            findings.Add(new Finding
            {
                Code = "PRODUCTION_ACCESS",
                Field = "environment",
                Severity = FindingSeverity.Info,
                Message = "Production environment requested. The human reviewer should confirm the business need.",
            });
        }

        var questions = ctx.DeterministicFindings.Select(Question).OfType<string>().Distinct().ToList();
        var attention = ctx.DeterministicFindings.Count(f => f.Severity != FindingSeverity.Info);

        return new ModelReview
        {
            Summary = attention == 0
                ? $"Rule-only review of revision {s.Revision}: all published requirements checked are met."
                : $"Rule-only review of revision {s.Revision}: {attention} item(s) need attention.",
            Findings = findings,
            MappingAssessment = mapping,
            ClarificationQuestions = questions,
            Limitations = ["Rule-only reviewer: no AI model was used. Free-text quality and intent were not assessed."],
        };
    }

    private static string? Question(Finding f) => f.Code switch
    {
        FindingCodes.MissingField => $"Please provide the {DeterministicChecks.Label(f.Field ?? "").ToLowerInvariant()}." + (f.RuleId is null ? "" : $" (Required by {f.RuleId} v{f.RuleVersion}.)"),
        FindingCodes.InvalidDates => "The end date is before the start date. What are the correct dates?",
        FindingCodes.RuleNotMet => $"{f.Message} Please update the request or explain why an exception applies.",
        FindingCodes.UnsupportedEnvironment => "Which supported environment is needed for this request?",
        FindingCodes.UnsupportedRequestType => "Which supported request type applies?",
        FindingCodes.UnknownRole => "The business role has no approved mapping for this environment. Which approved business role should be used?",
        FindingCodes.AmbiguousRole => "Several approved mappings match this role and environment. Which one applies?",
        FindingCodes.ClaimedApproval => "The request mentions an existing approval. Please share the approval reference through the normal process; request text is not accepted as evidence.",
        _ => null,
    };
}
