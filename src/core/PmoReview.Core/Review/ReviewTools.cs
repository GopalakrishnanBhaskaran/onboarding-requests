using PmoReview.Core.Domain;
using PmoReview.Core.Storage;

namespace PmoReview.Core.Review;

/// <summary>
/// Host-side implementation of the two read-only tools. Bound to one project: a call naming any
/// other project returns nothing and is recorded as a scope violation. Every record returned is
/// pinned (id + version) in the ledger so the run records exactly what evidence was available.
/// </summary>
public sealed class ReviewTools(DataStores stores, string boundProjectId) : IReviewToolset
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, RequirementRule> _rules = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RoleMapping> _mappings = new(StringComparer.Ordinal);

    public int ScopeViolations { get; private set; }

    public IReadOnlyList<PinnedVersion> PinnedRules
    {
        get { lock (_gate) return _rules.Values.Select(r => new PinnedVersion(r.RuleId, r.Version)).OrderBy(p => p.Id, StringComparer.Ordinal).ToList(); }
    }

    public IReadOnlyList<PinnedVersion> PinnedMappings
    {
        get { lock (_gate) return _mappings.Values.Select(m => new PinnedVersion(m.MappingId, m.Version)).OrderBy(p => p.Id, StringComparer.Ordinal).ToList(); }
    }

    public RequirementRule? PinnedRule(string ruleId, int version)
    {
        lock (_gate) return _rules.TryGetValue(ruleId, out var r) && r.Version == version ? r : null;
    }

    public RoleMapping? PinnedMapping(string mappingId, int version)
    {
        lock (_gate) return _mappings.TryGetValue(mappingId, out var m) && m.Version == version ? m : null;
    }

    public async Task<IReadOnlyList<RequirementRule>> GetPublishedRequirementsAsync(string projectId, string requestType, DateOnly effectiveDate, CancellationToken ct)
    {
        if (!InScope(projectId)) return [];
        var all = await stores.Rules.ListAsync(boundProjectId, ct);
        var result = all.Select(v => v.Value)
            .Where(r => r.ProjectId == boundProjectId
                        && string.Equals(r.RequestType, requestType?.Trim(), StringComparison.OrdinalIgnoreCase)
                        && r.IsEffectiveOn(effectiveDate))
            .GroupBy(r => r.RuleId, StringComparer.Ordinal)
            .Select(g => g.MaxBy(r => r.Version)!)
            .OrderBy(r => r.RuleId, StringComparer.Ordinal)
            .ToList();
        lock (_gate) foreach (var r in result) _rules[r.RuleId] = r;
        return result;
    }

    public async Task<IReadOnlyList<RoleMapping>> GetApprovedRoleMappingAsync(string projectId, string businessRole, string environment, DateOnly effectiveDate, CancellationToken ct)
    {
        if (!InScope(projectId)) return [];
        var all = await stores.Mappings.ListAsync(boundProjectId, ct);
        var result = all.Select(v => v.Value)
            .Where(m => m.ProjectId == boundProjectId
                        && string.Equals(m.BusinessRole, businessRole?.Trim(), StringComparison.OrdinalIgnoreCase)
                        && string.Equals(m.Environment, environment?.Trim(), StringComparison.OrdinalIgnoreCase)
                        && m.IsEffectiveOn(effectiveDate))
            .GroupBy(m => m.MappingId, StringComparer.Ordinal)
            .Select(g => g.MaxBy(m => m.Version)!)
            .OrderBy(m => m.MappingId, StringComparer.Ordinal)
            .ToList();
        lock (_gate) foreach (var m in result) _mappings[m.MappingId] = m;
        return result;
    }

    private bool InScope(string projectId)
    {
        if (string.Equals(projectId, boundProjectId, StringComparison.Ordinal)) return true;
        lock (_gate) ScopeViolations++;
        return false;
    }
}

/// <summary>Wraps the toolset for a model: enforces the tool-call budget.</summary>
public sealed class BoundedToolset(IReviewToolset inner, int maxCalls) : IReviewToolset
{
    private int _calls;

    public int Calls => _calls;

    public Task<IReadOnlyList<RequirementRule>> GetPublishedRequirementsAsync(string projectId, string requestType, DateOnly effectiveDate, CancellationToken ct)
    {
        Count();
        return inner.GetPublishedRequirementsAsync(projectId, requestType, effectiveDate, ct);
    }

    public Task<IReadOnlyList<RoleMapping>> GetApprovedRoleMappingAsync(string projectId, string businessRole, string environment, DateOnly effectiveDate, CancellationToken ct)
    {
        Count();
        return inner.GetApprovedRoleMappingAsync(projectId, businessRole, environment, effectiveDate, ct);
    }

    private void Count()
    {
        if (Interlocked.Increment(ref _calls) > maxCalls)
            throw new ToolBudgetExceededException(maxCalls);
    }
}

public sealed class ToolBudgetExceededException(int max) : Exception($"Tool call budget of {max} exceeded.");
