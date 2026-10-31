using Microsoft.Extensions.Logging;
using PmoReview.Core.Common;
using PmoReview.Core.Directory;
using PmoReview.Core.Domain;
using PmoReview.Core.Resources;
using PmoReview.Core.Services;
using PmoReview.Core.Storage;

namespace PmoReview.Core.Workflow;

/// <summary>
/// F1, worker only (the API cannot read Entra): reads each mapping group name of the agent's own tenant in Entra
/// (read-only, <see cref="IDirectoryReader.FindGroupsAsync"/>) and records the result; raises one UnresolvedGroup
/// escalation per group that does not resolve to an object id (never granted); and keeps the near-duplicate group names
/// as mapping quality issues for the Data Steward. Projects in another tenant are not read (tenant separation): there a
/// group resolves only by the object id on the sheet.
/// </summary>
public sealed class GroupResolutionJob(DataStores stores, IDirectoryReader? directory, IClock clock, ILogger<GroupResolutionJob> logger, string? agentTenantId,
    TimeSpan? refreshAfter = null, int maxReadsPerRun = 100)
{
    public const string Actor = "AI agent";
    private readonly TimeSpan refresh = refreshAfter ?? TimeSpan.FromHours(6);

    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var reads = 0;
        foreach (var p in await stores.Projects.ListAllAsync(ct))
        {
            try
            {
                reads += await RunProjectAsync(p.Value, maxReadsPerRun - reads, ct);
            }
            catch (DirectoryReadException e)
            {
                logger.LogWarning("group resolution: Entra read failed {Category} project={ProjectId}", e.Category, p.Value.ProjectId); // next run retries
            }
        }
        return reads;
    }

    private async Task<int> RunProjectAsync(Project project, int budget, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var rows = ResourceResolver.Offerable((await stores.MappingSheet.ListAsync(project.ProjectId, ct)).Select(v => v.Value).Where(r => r.ProjectId == project.ProjectId),
            null, DateOnly.FromDateTime(now.UtcDateTime));
        var known = (await stores.GroupResolutions.ListAsync(project.ProjectId, ct)).ToDictionary(v => v.Value.Id, StringComparer.Ordinal);
        var reads = 0;
        if (directory is not null && Tenants.Connected(project, agentTenantId))
        {
            foreach (var name in rows.Select(r => r.EntraGroup).Where(n => !string.IsNullOrWhiteSpace(n)).DistinctBy(ResourceResolver.Normalise))
            {
                var id = GroupResolution.MakeId(ResourceResolver.Normalise(name));
                if (known.TryGetValue(id, out var k) && now - k.Value.ReadAt < refresh) continue;
                if (reads >= budget) break;
                reads++;
                var groups = (await directory.FindGroupsAsync(ResourceResolver.Label(name), ct)).Where(g => g.SecurityEnabled).ToList();
                var doc = new GroupResolution
                {
                    Id = id, ProjectId = project.ProjectId, GroupName = ResourceResolver.Label(name), Count = groups.Count,
                    ObjectId = groups.Count == 1 ? groups[0].ObjectId : null, ReadAt = now,
                };
                known[id] = k is null ? await stores.GroupResolutions.CreateAsync(doc, ct) : await stores.GroupResolutions.ReplaceAsync(doc, k.ETag, ct);
            }
        }

        // One escalation per unresolved group (idempotent id), and the near-duplicate names as mapping quality issues.
        var lookups = known.Values.Select(v => v.Value).ToDictionary(v => v.Id, StringComparer.Ordinal);
        foreach (var row in rows.DistinctBy(r => ResourceResolver.Normalise(r.EntraGroup)))
        {
            var look = ResourceService.Lookup(row, lookups);
            if (look.Resolved) continue;
            await CreateOnceAsync(stores.Escalations, new Escalation
            {
                Id = Escalation.MakeId(EscalationKind.UnresolvedGroup, $"{project.ProjectId}|{ResourceResolver.Normalise(row.EntraGroup)}"),
                ProjectId = project.ProjectId, Kind = EscalationKind.UnresolvedGroup, Group = row.EntraGroup, Level = row.AccessLevel, Environment = row.Environment,
                Reason = $"Unresolved — never granted: {look.Why}. Correct the mapping sheet (object id) or have the ID team create the group.",
                RequestedBy = Actor, CreatedAt = now, IsSample = row.IsSample,
            }, ct);
        }
        foreach (var env in rows.Select(r => r.Environment).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var inEnv = ResourceResolver.Offerable(rows, env, DateOnly.FromDateTime(now.UtcDateTime));
            var options = ResourceResolver.Options(inEnv).Where(o => o != ResourceGroupTypes.All).ToList();
            if (options.Count == 0) continue;
            var notes = ResourceResolver.Resolve(inEnv, options, r => ResourceService.Lookup(r, lookups), env).Notes;
            foreach (var n in notes.Where(n => n.Reason.StartsWith("Near-duplicate", StringComparison.Ordinal)))
                await CreateOnceAsync(stores.MappingQualityIssues, new MappingQualityIssue
                {
                    Id = MappingQualityIssue.MakeId(n.VariantName, n.CanonicalName), ProjectId = project.ProjectId, VariantName = n.VariantName, CanonicalName = n.CanonicalName,
                    SheetRow = n.SheetRow, Reason = n.Reason, FirstSeenAt = now, LastSeenAt = now,
                }, ct);
        }
        return reads;
    }

    private static async Task CreateOnceAsync<T>(IDocumentStore<T> store, T doc, CancellationToken ct) where T : class, IDocument
    {
        if (await store.GetAsync(doc.PartitionKey, doc.Id, ct) is not null) return;
        try
        {
            await store.CreateAsync(doc, ct);
        }
        catch (DocumentConflictException)
        {
            // Created by a concurrent run.
        }
    }
}
