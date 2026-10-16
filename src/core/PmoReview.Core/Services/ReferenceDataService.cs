using PmoReview.Core.Common;
using PmoReview.Core.Domain;
using PmoReview.Core.Security;
using PmoReview.Core.Storage;

namespace PmoReview.Core.Services;

public sealed record RuleInput
{
    public string? RuleId { get; init; }
    public string? RequestType { get; init; }
    public string? Field { get; init; }
    public RuleKind? Rule { get; init; }
    public string? Parameter { get; init; }
    public string? RequirementText { get; init; }
    public DateOnly? EffectiveFrom { get; init; }
    public DateOnly? EffectiveTo { get; init; }
    public string? SourceReference { get; init; }
}

public sealed record MappingInput
{
    public string? MappingId { get; init; }
    public string? BusinessRole { get; init; }
    public string? Environment { get; init; }
    public string? GroupReference { get; init; }
    public DateOnly? EffectiveFrom { get; init; }
    public DateOnly? EffectiveTo { get; init; }
    public string? SourceReference { get; init; }
}

/// <summary>
/// Maintainer-only reference data. Published versions are immutable: a change is a new Draft
/// version, so every review can pin exactly what it used.
/// </summary>
public sealed class ReferenceDataService(DataStores stores, bool markSample = true)
{
    public async Task<IReadOnlyList<Versioned<RequirementRule>>> ListRulesAsync(CallerContext caller, string projectId, CancellationToken ct)
    {
        caller.Require(projectId, Permission.ViewReferenceData);
        return (await stores.Rules.ListAsync(projectId, ct))
            .Where(r => r.Value.ProjectId == projectId)
            .OrderBy(r => r.Value.RuleId, StringComparer.Ordinal).ThenBy(r => r.Value.Version).ToList();
    }

    public async Task<IReadOnlyList<Versioned<RoleMapping>>> ListMappingsAsync(CallerContext caller, string projectId, CancellationToken ct)
    {
        caller.Require(projectId, Permission.ViewReferenceData);
        return (await stores.Mappings.ListAsync(projectId, ct))
            .Where(m => m.Value.ProjectId == projectId)
            .OrderBy(m => m.Value.MappingId, StringComparer.Ordinal).ThenBy(m => m.Value.Version).ToList();
    }

    public async Task<Versioned<RequirementRule>> CreateRuleDraftAsync(CallerContext caller, string projectId, RuleInput input, CancellationToken ct)
    {
        caller.Require(projectId, Permission.ManageReferenceData);
        var ruleId = Required(input.RuleId, "ruleId");
        var field = Required(input.Field, "field");
        if (!RequestFields.All.Contains(field)) throw AppException.Invalid($"field must be one of: {string.Join(", ", RequestFields.All)}.");
        var kind = input.Rule ?? throw AppException.Invalid("rule is required.");
        ValidateParameter(kind, input.Parameter);
        var from = input.EffectiveFrom ?? throw AppException.Invalid("effectiveFrom is required.");
        if (input.EffectiveTo is { } to && to < from) throw AppException.Invalid("effectiveTo must be on or after effectiveFrom.");

        var existing = await stores.Rules.ListAsync(projectId, ct);
        var version = existing.Where(r => r.Value.RuleId == ruleId).Select(r => r.Value.Version).DefaultIfEmpty(0).Max() + 1;
        var doc = new RequirementRule
        {
            Id = $"{ruleId}:v{version}",
            ProjectId = projectId,
            RuleId = ruleId,
            Version = version,
            RequestType = Required(input.RequestType, "requestType"),
            Field = field,
            Rule = kind,
            Parameter = RequestService.Text(input.Parameter, "parameter", 500),
            RequirementText = Required(input.RequirementText, "requirementText", 1000),
            EffectiveFrom = from,
            EffectiveTo = input.EffectiveTo,
            Owner = caller.UserId,
            PublicationStatus = PublicationStatus.Draft,
            SourceReference = Required(input.SourceReference, "sourceReference"),
            IsSample = markSample,
        };
        return await CreateOrConflict(stores.Rules, doc, ct);
    }

    public async Task<Versioned<RoleMapping>> CreateMappingDraftAsync(CallerContext caller, string projectId, MappingInput input, CancellationToken ct)
    {
        caller.Require(projectId, Permission.ManageReferenceData);
        var mappingId = Required(input.MappingId, "mappingId");
        var from = input.EffectiveFrom ?? throw AppException.Invalid("effectiveFrom is required.");
        if (input.EffectiveTo is { } to && to < from) throw AppException.Invalid("effectiveTo must be on or after effectiveFrom.");

        var existing = await stores.Mappings.ListAsync(projectId, ct);
        var version = existing.Where(m => m.Value.MappingId == mappingId).Select(m => m.Value.Version).DefaultIfEmpty(0).Max() + 1;
        var doc = new RoleMapping
        {
            Id = $"{mappingId}:v{version}",
            ProjectId = projectId,
            MappingId = mappingId,
            Version = version,
            BusinessRole = Required(input.BusinessRole, "businessRole"),
            Environment = Required(input.Environment, "environment"),
            GroupReference = Required(input.GroupReference, "groupReference"),
            EffectiveFrom = from,
            EffectiveTo = input.EffectiveTo,
            Owner = caller.UserId,
            PublicationStatus = PublicationStatus.Draft,
            SourceReference = Required(input.SourceReference, "sourceReference"),
            IsSample = markSample,
        };
        return await CreateOrConflict(stores.Mappings, doc, ct);
    }

    public Task<Versioned<RequirementRule>> SetRuleStatusAsync(
        CallerContext caller, string projectId, string id, PublicationStatus target, string? ifMatch, CancellationToken ct) =>
        Transition(caller, projectId, id, target, ifMatch, stores.Rules, r => r.PublicationStatus, (r, s) => r with { PublicationStatus = s }, ct);

    public Task<Versioned<RoleMapping>> SetMappingStatusAsync(
        CallerContext caller, string projectId, string id, PublicationStatus target, string? ifMatch, CancellationToken ct) =>
        Transition(caller, projectId, id, target, ifMatch, stores.Mappings, m => m.PublicationStatus, (m, s) => m with { PublicationStatus = s }, ct);

    private static async Task<Versioned<T>> Transition<T>(
        CallerContext caller, string projectId, string id, PublicationStatus target, string? ifMatch,
        IDocumentStore<T> store, Func<T, PublicationStatus> status, Func<T, PublicationStatus, T> with, CancellationToken ct)
        where T : class, IDocument
    {
        caller.Require(projectId, Permission.ManageReferenceData);
        if (string.IsNullOrWhiteSpace(ifMatch)) throw AppException.EtagRequired();
        var current = await store.GetAsync(projectId, id, ct);
        if (current is null || current.Value.PartitionKey != projectId) throw AppException.NotFound("Reference record");
        if (current.ETag != ifMatch) throw AppException.Stale();

        var from = status(current.Value);
        var allowed = (from, target) is (PublicationStatus.Draft, PublicationStatus.Published)
                                     or (PublicationStatus.Published, PublicationStatus.Retired);
        if (!allowed) throw AppException.Invalid($"Cannot change status from {from} to {target}.");
        try
        {
            return await store.ReplaceAsync(with(current.Value, target), ifMatch, ct);
        }
        catch (ConcurrencyException)
        {
            throw AppException.Stale();
        }
    }

    private static async Task<Versioned<T>> CreateOrConflict<T>(IDocumentStore<T> store, T doc, CancellationToken ct) where T : class, IDocument
    {
        try
        {
            return await store.CreateAsync(doc, ct);
        }
        catch (DocumentConflictException)
        {
            throw new AppException(ErrorKind.Conflict, "version_conflict", "Another version was created at the same time. Try again.");
        }
    }

    private static void ValidateParameter(RuleKind kind, string? parameter)
    {
        switch (kind)
        {
            case RuleKind.MinLength or RuleKind.MaxDurationDays:
                if (!int.TryParse(parameter, out var n) || n <= 0) throw AppException.Invalid($"{kind} needs a positive whole-number parameter.");
                break;
            case RuleKind.AllowedValues:
                if (string.IsNullOrWhiteSpace(parameter) || parameter.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length == 0)
                    throw AppException.Invalid("AllowedValues needs a pipe-separated parameter, e.g. \"Dev|Test\".");
                break;
        }
    }

    private static string Required(string? value, string field, int max = 200) =>
        RequestService.Text(value, field, max) ?? throw AppException.Invalid($"{field} is required.");
}
