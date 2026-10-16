using PmoReview.Core.Domain;
using PmoReview.Core.Storage;

namespace PmoReview.Core.Review;

public sealed record EngineOutcome(
    ReviewResult Result,
    IReadOnlyList<PinnedVersion> RuleVersions,
    IReadOnlyList<PinnedVersion> MappingVersions,
    ReviewerInfo Reviewer);

/// <summary>
/// Bounded review: deterministic checks → host mapping check → reviewer (RuleOnly or model) →
/// host validation → schema validation. The reviewer never decides readiness.
/// </summary>
public sealed class ReviewEngine(DataStores stores, IReviewModel model, ReviewLimits limits)
{
    public ReviewerInfo Reviewer => model.Info;

    public async Task<EngineOutcome> RunAsync(RequestSnapshot snapshot, string reviewRunId, CancellationToken ct)
    {
        var tools = new ReviewTools(stores, snapshot.ProjectId);
        var project = (await stores.Projects.GetAsync(snapshot.ProjectId, snapshot.ProjectId, ct))?.Value;
        var effectiveDate = snapshot.StartDate ?? DateOnly.FromDateTime(snapshot.CapturedAt.UtcDateTime);

        var deterministic = await DeterministicChecks.RunAsync(snapshot, project, effectiveDate, tools, ct);
        var (mapping, mappingFindings) = await AssessMappingAsync(snapshot, project, effectiveDate, tools, ct);
        var hostFindings = deterministic.Findings.Concat(mappingFindings).ToList();

        ModelReview review;
        using (var budget = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            budget.CancelAfter(limits.TimeBudget);
            var context = new ReviewContext(snapshot, reviewRunId, effectiveDate, hostFindings, new BoundedToolset(tools, limits.MaxToolCalls));
            try
            {
                review = await model.ReviewAsync(context, limits, budget.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new ReviewFailedException(ErrorCategory.Timeout, "Reviewer exceeded its time budget.");
            }
            catch (ReviewFailedException)
            {
                throw;
            }
            catch (ToolBudgetExceededException e)
            {
                throw new ReviewFailedException(ErrorCategory.MalformedResult, "Reviewer exceeded its tool budget.", e);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                throw new ReviewFailedException(ErrorCategory.DependencyUnavailable, "Reviewer failed.", e);
            }
        }

        var result = ResultAssembler.Assemble(reviewRunId, snapshot, deterministic, hostFindings, mapping, review, tools);
        ResultSchema.EnsureValid(result);
        return new EngineOutcome(result, tools.PinnedRules, tools.PinnedMappings, model.Info);
    }

    /// <summary>Host-computed mapping status from pinned, published mappings only.</summary>
    public static async Task<(MappingAssessment, IReadOnlyList<Finding>)> AssessMappingAsync(
        RequestSnapshot s, Project? project, DateOnly effectiveDate, IReviewToolset tools, CancellationToken ct)
    {
        if (project is null || string.IsNullOrWhiteSpace(s.Role) || string.IsNullOrWhiteSpace(s.Environment))
        {
            return (new MappingAssessment { Status = MappingStatus.NotApplicable, Explanation = "Mapping not assessed: project, role or environment is missing or unsupported." }, []);
        }

        var matches = await tools.GetApprovedRoleMappingAsync(s.ProjectId, s.Role, s.Environment, effectiveDate, ct);
        return matches.Count switch
        {
            0 => (new MappingAssessment { Status = MappingStatus.NotFound, Explanation = $"No published role mapping is in effect for this business role and environment on {effectiveDate:yyyy-MM-dd}." },
                [new Finding { Code = FindingCodes.UnknownRole, Field = "role", Severity = FindingSeverity.Blocking, Message = "Business role has no approved mapping for the requested environment." }]),
            1 => (new MappingAssessment { Status = MappingStatus.Matched, MappingId = matches[0].MappingId, MappingVersion = matches[0].Version, Explanation = $"Matches published mapping {matches[0].MappingId} v{matches[0].Version} ({matches[0].SourceReference})." }, []),
            _ => (new MappingAssessment { Status = MappingStatus.Ambiguous, Explanation = $"{matches.Count} published mappings match ({string.Join(", ", matches.Select(m => $"{m.MappingId} v{m.Version}"))}). A human must decide which applies." },
                [new Finding { Code = FindingCodes.AmbiguousRole, Field = "role", Severity = FindingSeverity.Blocking, Message = "More than one published mapping matches this role and environment." }]),
        };
    }
}
