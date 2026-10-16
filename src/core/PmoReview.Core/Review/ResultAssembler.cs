using System.Text.RegularExpressions;
using PmoReview.Core.Domain;
using PmoReview.Core.Services;

namespace PmoReview.Core.Review;

/// <summary>
/// The host's final say. Deterministic findings are kept as-is; reviewer output is treated as
/// untrusted: citations must resolve to pinned versions, the mapping must match pinned data,
/// text is length-capped, and readiness is computed here, never taken from the reviewer.
/// </summary>
public static partial class ResultAssembler
{
    public const string BaseLimitation = "This is an automated readiness check. It does not approve or grant access.";

    public static ReviewResult Assemble(
        string reviewRunId,
        RequestSnapshot snapshot,
        DeterministicOutcome deterministic,
        IReadOnlyList<Finding> hostFindings,
        MappingAssessment hostMapping,
        ModelReview review,
        ReviewTools ledger)
    {
        var findings = new List<Finding>(hostFindings);
        var limitations = new List<string> { BaseLimitation };
        var rejectedCitations = 0;

        // Reviewer findings: validate, cap, de-duplicate.
        foreach (var raw in review.Findings.Take(20))
        {
            var f = Clean(raw);
            if (f is null) continue;
            if (f.RuleId is not null || f.RuleVersion is not null)
            {
                var pinned = f.RuleId is null || f.RuleVersion is null ? null : ledger.PinnedRule(f.RuleId, f.RuleVersion.Value);
                if (pinned is null)
                {
                    rejectedCitations++;
                    continue;
                }
                f = f with { SourceReference = pinned.SourceReference }; // host truth, not reviewer text
            }
            else
            {
                f = f with { SourceReference = null };
            }
            if (findings.Any(x => x.Code == f.Code && x.Field == f.Field)) continue;
            findings.Add(f);
        }

        // Mapping: host assessment is authoritative; reviewer explanation kept only when it agrees.
        var mapping = hostMapping;
        if (review.MappingAssessment is { } proposed)
        {
            var agrees = proposed.Status == hostMapping.Status
                         && proposed.MappingId == hostMapping.MappingId
                         && proposed.MappingVersion == hostMapping.MappingVersion;
            if (agrees)
            {
                mapping = hostMapping with { Explanation = Text(proposed.Explanation, 500) ?? hostMapping.Explanation };
            }
            else
            {
                if (proposed.MappingId is not null && (proposed.MappingVersion is null || ledger.PinnedMapping(proposed.MappingId, proposed.MappingVersion.Value) is null))
                    rejectedCitations++;
                limitations.Add("The reviewer's mapping assessment did not match the pinned mapping data and was replaced by the host assessment.");
            }
        }

        if (rejectedCitations > 0)
        {
            findings.Add(new Finding
            {
                Code = FindingCodes.UnsupportedCitation,
                Severity = FindingSeverity.Warning,
                Message = $"{rejectedCitations} reviewer citation(s) did not match a pinned, published version and were removed.",
            });
        }

        if (ledger.ScopeViolations > 0)
        {
            findings.Add(new Finding
            {
                Code = FindingCodes.ToolScopeViolation,
                Severity = FindingSeverity.Warning,
                Message = "The reviewer asked for reference data outside this request's project. Nothing was returned.",
            });
        }

        foreach (var l in review.Limitations.Take(5))
            if (Text(l, 300) is { } t && !limitations.Contains(t)) limitations.Add(t);

        // Readiness: Manual > NeedsInformation > Ready. A missing critical field can never be Ready.
        var manual = deterministic.ManualReviewTrigger
                     || mapping.Status == MappingStatus.Ambiguous
                     || rejectedCitations > 0
                     || ledger.ScopeViolations > 0;
        var blocking = deterministic.MissingCriticalField || findings.Any(f => f.Severity == FindingSeverity.Blocking);
        var readiness = manual ? Readiness.ManualReviewRequired
            : blocking ? Readiness.NeedsInformation
            : Readiness.ReadyForHumanReview;

        var questions = review.ClarificationQuestions
            .Select(q => Text(q, 300))
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .ToList();
        if (readiness == Readiness.NeedsInformation && questions.Count == 0)
            questions.Add("Please complete or correct the items listed in the findings.");

        var label = readiness switch
        {
            Readiness.ReadyForHumanReview => "Ready for human review (not an approval)",
            Readiness.NeedsInformation => "Needs information",
            _ => "Manual review required",
        };
        var blockingCount = findings.Count(f => f.Severity == FindingSeverity.Blocking);
        var warningCount = findings.Count(f => f.Severity == FindingSeverity.Warning);
        var reviewerSummary = Text(review.Summary, 700);
        var summary = $"{label}. {blockingCount} blocking, {warningCount} warning finding(s)." + (reviewerSummary is null ? "" : $" {reviewerSummary}");

        return new ReviewResult
        {
            SchemaVersion = ReviewResult.CurrentSchemaVersion,
            ReviewRunId = reviewRunId,
            RequestRevision = snapshot.Revision,
            Readiness = readiness,
            Summary = summary,
            Findings = findings.Take(50).ToList(),
            MappingAssessment = mapping,
            ClarificationQuestions = questions,
            HumanReviewRequired = true,
            Limitations = limitations.Take(20).ToList(),
        };
    }

    private static Finding? Clean(Finding f)
    {
        var code = f.Code?.Trim().ToUpperInvariant() ?? "";
        if (!Code().IsMatch(code)) return null;
        var message = Text(f.Message, 500);
        if (message is null) return null;
        return new Finding
        {
            Code = code,
            Field = f.Field is not null && RequestFields.All.Contains(f.Field) ? f.Field : null,
            Severity = f.Severity,
            Message = message,
            RuleId = Text(f.RuleId, 128),
            RuleVersion = f.RuleVersion,
            SourceReference = f.SourceReference,
        };
    }

    /// <summary>Strips control characters and caps length. Returns null for empty text.</summary>
    public static string? Text(string? value, int max)
    {
        if (value is null) return null;
        var cleaned = new string(value.Where(c => !char.IsControl(c) || c == '\n').ToArray()).Trim();
        if (cleaned.Length == 0) return null;
        return cleaned.Length <= max ? cleaned : cleaned[..(max - 1)] + "…";
    }

    [GeneratedRegex("^[A-Z][A-Z_]{2,39}$")]
    private static partial Regex Code();
}
