using System.Text.RegularExpressions;
using PmoReview.Core.Common;
using PmoReview.Core.Domain;
using PmoReview.Core.Security;
using PmoReview.Core.Storage;

namespace PmoReview.Core.Services;

public sealed record MappingRowInput
{
    public string? RowId { get; init; }
    public string? Resource { get; init; }
    public string? AccessLevel { get; init; }
    public string? EntraGroup { get; init; }
    public string? Role { get; init; }
    public string? RoleScope { get; init; }
    public bool? Privileged { get; init; }
    public bool? SensitiveResource { get; init; }
    public bool? Deprecated { get; init; }
    public GroupState? GroupState { get; init; }
    public string? SourceReference { get; init; }
}

/// <summary>ID team: records the group it created (or the corrected name) on the mapping sheet.</summary>
public sealed record MappingGroupInput(string? EntraGroup, GroupState? GroupState);

/// <summary>
/// The mapping sheet (PMO-owned) and the response document, as read and edited through the app. Every change bumps the
/// row version, so in-flight requests that depend on the row are reviewed again before they continue.
/// </summary>
public sealed partial class MappingSheetService(DataStores stores, IClock clock, bool markSample = true)
{
    public async Task<IReadOnlyList<Versioned<MappingRow>>> ListAsync(CallerContext caller, string projectId, CancellationToken ct)
    {
        caller.Require(projectId, Permission.ViewMappingSheet);
        return (await stores.MappingSheet.ListAsync(projectId, ct))
            .Where(r => r.Value.ProjectId == projectId)
            .OrderBy(r => r.Value.Resource, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Value.AccessLevel, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Value.RowId, StringComparer.Ordinal).ToList();
    }

    public async Task<Versioned<MappingRow>> CreateAsync(CallerContext caller, string projectId, MappingRowInput input, CancellationToken ct)
    {
        caller.Require(projectId, Permission.EditMappingSheet);
        var rowId = RequestService.Text(input.RowId, "rowId", 64) ?? $"ROW-{Guid.NewGuid():N}"[..12].ToUpperInvariant();
        if (!RowIdFormat().IsMatch(rowId)) throw AppException.Invalid("rowId may contain only letters, digits, dot, colon, dash and underscore.");
        var row = Apply(new MappingRow
        {
            Id = rowId, ProjectId = projectId, RowId = rowId, Resource = "", AccessLevel = "", EntraGroup = "", Role = "",
            SourceReference = "", Version = 1, UpdatedBy = caller.UserId, UpdatedAt = clock.UtcNow, IsSample = markSample,
        }, input);
        try
        {
            return await stores.MappingSheet.CreateAsync(row, ct);
        }
        catch (DocumentConflictException)
        {
            throw AppException.Conflict("row_exists", $"Mapping row {rowId} already exists. Change it instead.");
        }
    }

    public async Task<Versioned<MappingRow>> UpdateAsync(CallerContext caller, string projectId, string rowId, MappingRowInput input, string? ifMatch, CancellationToken ct)
    {
        caller.Require(projectId, Permission.EditMappingSheet);
        var current = await LoadForUpdateAsync(projectId, rowId, ifMatch, ct);
        var next = Apply(current.Value, input) with { Version = current.Value.Version + 1, UpdatedBy = caller.UserId, UpdatedAt = clock.UtcNow };
        return await ReplaceAsync(next, ifMatch!, ct);
    }

    /// <summary>ID team: group name and state only ("ID team creates the group if missing and updates the mapping sheet").</summary>
    public async Task<Versioned<MappingRow>> UpdateGroupAsync(CallerContext caller, string projectId, string rowId, MappingGroupInput input, string? ifMatch, CancellationToken ct)
    {
        caller.Require(projectId, Permission.UpdateMappingGroup);
        var current = await LoadForUpdateAsync(projectId, rowId, ifMatch, ct);
        var group = RequestService.Text(input.EntraGroup, "entraGroup", 256) ?? current.Value.EntraGroup;
        var next = current.Value with
        {
            EntraGroup = group,
            GroupState = input.GroupState ?? current.Value.GroupState,
            Version = current.Value.Version + 1,
            UpdatedBy = caller.UserId,
            UpdatedAt = clock.UtcNow,
        };
        return await ReplaceAsync(next, ifMatch!, ct);
    }

    public async Task<IReadOnlyList<Versioned<ResponseRecord>>> ListResponseDocumentAsync(CallerContext caller, string projectId, CancellationToken ct)
    {
        caller.Require(projectId, Permission.ViewResponseDocument);
        return (await stores.ResponseDocument.ListAsync(projectId, ct))
            .Where(r => r.Value.ProjectId == projectId).OrderByDescending(r => r.Value.UpdatedAt).ToList();
    }

    private async Task<Versioned<MappingRow>> LoadForUpdateAsync(string projectId, string rowId, string? ifMatch, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(ifMatch)) throw AppException.EtagRequired();
        var current = await stores.MappingSheet.GetAsync(projectId, rowId, ct);
        if (current is null || current.Value.ProjectId != projectId) throw AppException.NotFound("Mapping row");
        if (current.ETag != ifMatch) throw AppException.Stale();
        return current;
    }

    private async Task<Versioned<MappingRow>> ReplaceAsync(MappingRow next, string ifMatch, CancellationToken ct)
    {
        try
        {
            return await stores.MappingSheet.ReplaceAsync(next, ifMatch, ct);
        }
        catch (ConcurrencyException)
        {
            throw AppException.Stale();
        }
    }

    private static MappingRow Apply(MappingRow row, MappingRowInput input) => row with
    {
        Resource = Required(input.Resource, "resource"),
        AccessLevel = Required(input.AccessLevel, "accessLevel"),
        EntraGroup = Required(input.EntraGroup, "entraGroup", 256),
        Role = Required(input.Role, "role"),
        RoleScope = RequestService.Text(input.RoleScope, "roleScope", 500),
        Privileged = input.Privileged ?? false,
        SensitiveResource = input.SensitiveResource ?? false,
        Deprecated = input.Deprecated ?? false,
        GroupState = input.GroupState ?? GroupState.Exists,
        SourceReference = Required(input.SourceReference, "sourceReference"),
    };

    private static string Required(string? value, string field, int max = 200) =>
        RequestService.Text(value, field, max) ?? throw AppException.Invalid($"{field} is required.");

    [GeneratedRegex("^[A-Za-z0-9_.:-]{1,64}$")]
    private static partial Regex RowIdFormat();
}
