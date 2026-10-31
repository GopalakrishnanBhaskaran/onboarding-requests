using System.Security.Cryptography;
using System.Text;
using PmoReview.Core.Common;
using PmoReview.Core.Domain;
using PmoReview.Core.Resources;
using PmoReview.Core.Security;
using PmoReview.Core.Storage;

namespace PmoReview.Core.Services;

/// <summary>Resource* options of one tenant (not part of the resolve contract; the form reads it first).</summary>
public sealed record ResourceOptions(string TenantId, string Environment, IReadOnlyList<string> Options, string AllOption);

/// <summary>
/// F1: the Resource* selector over the signed-in user's tenant mapping sheet. Every query is filtered server-side by the
/// project (one project = one tenant) the caller holds a role in: another tenant's rows are never read, and asking for
/// a project the caller has no role in is a 404. Read-only: escalations for unresolved groups are raised by the worker.
/// </summary>
/// <param name="localDevUnverified">Local development only (ALLOW_UNVERIFIED_ENTRA, needs PILOT_LOCAL_DEV): no worker reads Entra,
/// so groups count as resolved, "not checked (local development)", like the other Entra checks there.</param>
public sealed class ResourceService(DataStores stores, IClock clock, string? agentTenantId, bool localDevUnverified = false)
{
    public async Task<ResourceOptions> OptionsAsync(CallerContext caller, string? projectId, string? environment, CancellationToken ct)
    {
        var (project, env, rows) = await LoadAsync(caller, projectId, environment, ct);
        return new ResourceOptions(TenantIdOf(project), env, ResourceResolver.Options(rows), ResourceGroupTypes.All);
    }

    public async Task<(ResolveResult Result, Resolution Resolution)> ResolveAsync(CallerContext caller, string? projectId, string? environment,
        IReadOnlyList<string> selected, CancellationToken ct)
    {
        var (project, env, rows) = await LoadAsync(caller, projectId, environment, ct);
        var options = ResourceResolver.Options(rows);
        IReadOnlyList<string> selection;
        try
        {
            selection = ResourceResolver.Expand(selected, options);
        }
        catch (UnknownResourceException e)
        {
            throw AppException.Invalid($"Not a resource of {Tenants.Label(project)}: {string.Join(", ", e.Unknown)}. Choose from the tenant's mapping sheet.");
        }
        if (selection.Count == 0) throw AppException.Invalid("Choose at least one resource.");
        var resolutions = (await stores.GroupResolutions.ListAsync(project.ProjectId, ct)).Select(v => v.Value).Where(x => x.ProjectId == project.ProjectId)
            .ToDictionary(x => x.Id, StringComparer.Ordinal);
        var resolution = ResourceResolver.Resolve(rows, selection,
            r => localDevUnverified ? new GroupLookup(true, Guid.TryParse(r.GroupObjectId, out var g) ? g.ToString("D") : null, "not checked (local development)") : Lookup(r, resolutions),
            env.Length == 0 ? null : env);
        var result = new ResolveResult(TenantIdOf(project), env, selection, resolution.Entries.Select(e => e.Access).ToList(), resolution.Notes,
            await MappingVersionAsync(project.ProjectId, ct));
        return (result, resolution);
    }

    /// <summary>
    /// What is known about a group in Entra: the worker's last read of the name (it alone can read Entra), else the sheet's
    /// object id. A group the sheet says is still "to be created", a name Entra does not know (or knows twice), or a sheet
    /// id Entra contradicts is unresolved: never granted.
    /// </summary>
    public static GroupLookup Lookup(MappingRow r, IReadOnlyDictionary<string, GroupResolution> resolutions)
    {
        var sheetId = Guid.TryParse(r.GroupObjectId, out var g) ? g.ToString("D") : null;
        if (resolutions.TryGetValue(GroupResolution.MakeId(ResourceResolver.Normalise(r.EntraGroup)), out var read))
        {
            if (read.Count == 0) return new GroupLookup(false, null, $"{r.EntraGroup} is not in Entra (read {read.ReadAt:yyyy-MM-dd HH:mm} UTC)");
            if (read.Count > 1) return new GroupLookup(false, null, $"{read.Count} groups in Entra are named {r.EntraGroup}");
            if (sheetId is not null && !string.Equals(sheetId, read.ObjectId, StringComparison.OrdinalIgnoreCase))
                return new GroupLookup(false, null, $"the mapping sheet says object id {sheetId}, Entra has {read.ObjectId} for {r.EntraGroup}");
            return new GroupLookup(true, read.ObjectId, "resolved in Entra");
        }
        if (r.GroupState == GroupState.ToBeCreated) return new GroupLookup(false, null, $"{r.EntraGroup} is still to be created by the ID team");
        return sheetId is not null ? new GroupLookup(true, sheetId, "object id from the mapping sheet")
            : new GroupLookup(false, null, $"{r.EntraGroup} has no object id on the mapping sheet and has not been found in Entra yet");
    }

    /// <summary>"v{n}" of the latest applied upload; before any upload, a hash of the rows (id + version).</summary>
    public async Task<string> MappingVersionAsync(string projectId, CancellationToken ct)
    {
        var latest = (await stores.MappingVersions.ListAsync(projectId, ct)).Select(v => v.Value)
            .Where(v => v.ProjectId == projectId && v.Part == 0).OrderByDescending(v => v.Version).FirstOrDefault();
        if (latest is not null) return $"v{latest.Version}";
        var rows = (await stores.MappingSheet.ListAsync(projectId, ct)).Select(v => v.Value).Where(r => r.ProjectId == projectId)
            .OrderBy(r => r.RowId, StringComparer.Ordinal).Select(r => $"{r.RowId}:{r.Version}");
        return "sheet-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|", rows))))[..12];
    }

    private string TenantIdOf(Project p) => p.TenantId ?? agentTenantId ?? p.ProjectId;

    private async Task<(Project Project, string Environment, IReadOnlyList<MappingRow> Rows)> LoadAsync(CallerContext caller, string? projectId, string? environment, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(projectId)) throw AppException.Invalid("projectId (the tenant) is required.");
        // A project (tenant) the caller has no role in is "not found": tenant A never sees whether tenant B exists.
        caller.Require(projectId, Permission.ViewMappingSheet);
        var project = (await stores.Projects.GetAsync(projectId, projectId, ct))?.Value ?? throw AppException.NotFound("Project");
        string env;
        if (project.Environments.Count > 0)
        {
            env = Environments.Canonical(project, environment)
                  ?? throw AppException.Invalid($"environment must be one of {string.Join(", ", project.Environments)}.");
        }
        else
        {
            env = string.IsNullOrWhiteSpace(environment) ? "" : throw AppException.Invalid($"{Tenants.Label(project)} has no environments.");
        }
        // Defence in depth: a row whose I1 TenantId names another tenant is never offered (uploads refuse it too).
        var tenant = project.TenantId ?? agentTenantId;
        var rows = (await stores.MappingSheet.ListAsync(project.ProjectId, ct)).Select(v => v.Value)
            .Where(r => r.ProjectId == project.ProjectId && (r.TenantId is null || tenant is null || string.Equals(r.TenantId.Trim(), tenant, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        return (project, env, ResourceResolver.Offerable(rows, env.Length == 0 ? null : env, DateOnly.FromDateTime(clock.UtcNow.UtcDateTime)));
    }
}
