using PmoReview.Core.Common;
using PmoReview.Core.Resources;
using PmoReview.Core.Security;
using PmoReview.Core.Services;

namespace PmoReview.Core.Workflow;

/// <summary>
/// F1 submit: the intake fields once, the Resource* selection and the resolved groups the requester ticked. Identity,
/// project and bundle id never come from the body.
/// </summary>
public sealed record BundleInput
{
    public string? EnterpriseId { get; init; }
    public string? FirstName { get; init; }
    public string? LastName { get; init; }
    public string? Environment { get; init; }
    public string? Justification { get; init; }
    public string? Owner { get; init; }
    public bool? Guest { get; init; }
    public IReadOnlyList<string>? SelectedResources { get; init; }
    /// <summary>Resolved groups to request (object id or group name as shown). Null = every requestable group of the list.</summary>
    public IReadOnlyList<string>? Groups { get; init; }
}

public sealed record BundleRequest(string RequestId, string GroupName, string AccessLevel, string ResolveStatus, StatusLine Status);

public sealed record BundleSkipped(string GroupName, string Reason);

public sealed record BundleResult(string BundleId, IReadOnlyList<BundleRequest> Requests, IReadOnlyList<BundleSkipped> Skipped);

/// <summary>
/// F1: one request per resolved group (decision 10), each through the whole audited flow (AI review, Owner, Security,
/// ProdApprover for Prod above Read, teams or the agent, verification). Unresolved groups are never requested.
/// </summary>
public sealed class RequestBundleService(ResourceService resources, WorkflowService workflow)
{
    public const int MaxGroups = 20;

    public async Task<BundleResult> SubmitAsync(CallerContext caller, string projectId, BundleInput input, string cid, CancellationToken ct)
    {
        caller.Require(projectId, Permission.SubmitRequests);
        var selected = input.SelectedResources ?? [];
        if (selected.Count == 0) throw AppException.Invalid("Resource* is required: choose at least one resource.");
        var (_, resolution) = await resources.ResolveAsync(caller, projectId, input.Environment, selected, ct);

        var wanted = input.Groups?.Where(g => !string.IsNullOrWhiteSpace(g)).Select(g => g.Trim()).ToList();
        bool Picked(ResolvedEntry e) => wanted is null
            || wanted.Any(w => (e.Access.GroupObjectId.Length > 0 && string.Equals(w, e.Access.GroupObjectId, StringComparison.OrdinalIgnoreCase))
                               || ResourceResolver.Normalise(w) == ResourceResolver.Normalise(e.Access.GroupName));
        var unknown = wanted?.Where(w => !resolution.Entries.Any(e => (e.Access.GroupObjectId.Length > 0 && string.Equals(w, e.Access.GroupObjectId, StringComparison.OrdinalIgnoreCase))
                                                                      || ResourceResolver.Normalise(w) == ResourceResolver.Normalise(e.Access.GroupName))).ToList() ?? [];
        if (unknown.Count > 0) throw AppException.Invalid($"Not in the resolved list for this selection: {string.Join(", ", unknown)}.");

        var picked = resolution.Entries.Where(Picked).ToList();
        var skipped = picked.Where(e => e.Access.Status == ResolveStatus.Unresolved)
            .Select(e => new BundleSkipped(e.Access.GroupName, "Unresolved — escalated: the group has no Entra object id, so it is never granted.")).ToList();
        var requestable = picked.Where(e => e.Access.Status != ResolveStatus.Unresolved).ToList();
        if (requestable.Count == 0) throw AppException.Invalid("Choose at least one resolved group that is not Unresolved.");
        if (requestable.Count > MaxGroups) throw AppException.Invalid($"At most {MaxGroups} groups per submission.");

        // Validate the shared intake once before anything is saved, so a bad field never leaves half a bundle.
        RequestService.Validate(ToInput(input, requestable[0]));
        var bundleId = Guid.NewGuid().ToString("n");
        var created = new List<BundleRequest>();
        foreach (var e in requestable)
        {
            var view = await workflow.SubmitAsync(caller, projectId, ToInput(input, e), cid, ct, bundleId);
            created.Add(new BundleRequest(view.Request.Value.Id, e.Access.GroupName, e.Access.AccessLevel, e.Access.Status, view.Status));
        }
        return new BundleResult(bundleId, created, skipped);
    }

    /// <summary>The request for one resolved group: the mapping row's own resource and level (so the AI review finds that row).</summary>
    private static RequestInput ToInput(BundleInput input, ResolvedEntry e) => new()
    {
        EnterpriseId = input.EnterpriseId, FirstName = input.FirstName, LastName = input.LastName, Environment = input.Environment,
        Justification = input.Justification, Owner = input.Owner, Guest = input.Guest, Resource = e.Row.Resource, AccessLevel = e.Row.AccessLevel,
    };
}
