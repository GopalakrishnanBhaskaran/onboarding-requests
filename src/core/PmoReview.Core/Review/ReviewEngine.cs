using PmoReview.Core.Common;
using PmoReview.Core.Directory;
using PmoReview.Core.Domain;
using PmoReview.Core.Sources;
using PmoReview.Core.Storage;

namespace PmoReview.Core.Review;

public sealed record EngineOutcome(
    ReviewResult Result,
    IReadOnlyList<PinnedVersion> MappingRows,
    string SheetFingerprint,
    ReviewerInfo Reviewer,
    TokenUsage? Usage);

/// <summary>
/// Stage 2, the mandatory gate before any approval request: read the mapping sheet and the response document fresh,
/// run the host checks (with the read-only Entra cross-check when a directory reader is available), let the reviewer
/// add concerns, then compute the outcome and validate the result against the schema.
/// </summary>
public sealed class ReviewEngine(DataStores stores, ISourcesOfTruth sources, IReviewModel model, ReviewLimits limits, ReviewPolicy policy,
    IDirectoryReader? directory = null, bool skipEntraLocalDev = false, string? agentTenantId = null)
{
    public ReviewerInfo Reviewer => model.Info;

    /// <summary>False in the SWA-managed API (no managed identity): the Entra checks stay Pending until the worker runs them.</summary>
    public bool CanCrossCheck => directory is not null;

    public async Task<EngineOutcome> RunAsync(RequestSnapshot snapshot, string reviewRunId, CancellationToken ct)
    {
        Project? project;
        IReadOnlyList<MappingRow> sheet;
        IReadOnlyList<ResponseRecord> responses;
        try
        {
            project = (await stores.Projects.GetAsync(snapshot.ProjectId, snapshot.ProjectId, ct))?.Value;
            sheet = await sources.ReadMappingSheetAsync(snapshot.ProjectId, ct);
            responses = await sources.ReadResponseDocumentAsync(snapshot.ProjectId, ct);
        }
        catch (SourceUnavailableException e)
        {
            throw new ReviewFailedException(ErrorCategory.DependencyUnavailable, $"{e.SourceName} unavailable.", e);
        }

        var match = ReviewChecks.Match(snapshot, sheet);
        // Tenant separation: only the process with the Entra reader (the worker, which knows its tenant) decides that a
        // project's tenant is not its own; the website leaves the check pending for it.
        var notConnected = directory is null ? null : Tenants.NotConnected(project, agentTenantId);
        var entra = notConnected is null ? await CrossCheckAsync(snapshot, match.Row, ct) : null;
        var host = ReviewChecks.Evaluate(snapshot, project, sheet, responses, match, entra, policy, skipEntraLocalDev && directory is null, notConnected);

        ModelReview review;
        using (var budget = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            budget.CancelAfter(limits.TimeBudget);
            var context = new ReviewContext(snapshot, reviewRunId, host.Checks, host.RiskFlags, host.Mapping, host.UserRecords);
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
            catch (Exception e) when (e is not OperationCanceledException)
            {
                throw new ReviewFailedException(ErrorCategory.DependencyUnavailable, "Reviewer failed.", e);
            }
        }

        var result = ResultAssembler.Assemble(reviewRunId, snapshot, host, review);
        ResultSchema.EnsureValid(result);
        var pinned = match.ResourceRows.Select(r => new PinnedVersion(r.RowId, r.Version)).ToList();
        return new EngineOutcome(result, pinned, MappingSheet.Fingerprint(sheet, snapshot.Resource), model.Info, review.Usage);
    }

    private async Task<CrossCheck?> CrossCheckAsync(RequestSnapshot s, MappingRow? row, CancellationToken ct)
    {
        if (directory is null || row is null) return null;
        try
        {
            var groups = await directory.FindGroupsAsync(row.EntraGroup, ct);
            var user = string.IsNullOrWhiteSpace(s.DomainId) ? null : await directory.FindUserAsync(s.DomainId, ct);
            return new CrossCheck(groups, user, null);
        }
        catch (DirectoryReadException e)
        {
            return new CrossCheck([], null, e.Category);
        }
    }
}
