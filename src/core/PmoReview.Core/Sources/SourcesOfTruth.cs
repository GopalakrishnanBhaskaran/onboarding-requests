using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PmoReview.Core.Domain;
using PmoReview.Core.Storage;

namespace PmoReview.Core.Sources;

/// <summary>A source of truth could not be read after the allowed retries.</summary>
public sealed class SourceUnavailableException(string source, Exception inner) : Exception($"{source} could not be read.", inner)
{
    public string SourceName { get; } = source;
}

/// <summary>
/// The two sources of truth: the PMO-to-Entra Group Mapping Sheet and the Response Document.
/// Read fresh on every call: nothing is hardcoded or cached across requests. The pilot keeps both in Cosmos; another
/// implementation (e.g. a SharePoint list) can replace this one without touching the review or the workflow.
/// </summary>
public interface ISourcesOfTruth
{
    Task<IReadOnlyList<MappingRow>> ReadMappingSheetAsync(string projectId, CancellationToken ct);
    Task<IReadOnlyList<ResponseRecord>> ReadResponseDocumentAsync(string projectId, CancellationToken ct);
}

public sealed class StoreSourcesOfTruth(DataStores stores) : ISourcesOfTruth
{
    /// <summary>"Refresh sheet read" auto-fix: a failed read is retried, at most 3 attempts in total.</summary>
    public const int MaxAttempts = 3;

    public Task<IReadOnlyList<MappingRow>> ReadMappingSheetAsync(string projectId, CancellationToken ct) =>
        ReadAsync("Mapping sheet", async () => (await stores.MappingSheet.ListAsync(projectId, ct))
            .Select(v => v.Value).Where(r => r.ProjectId == projectId).ToList(), ct);

    public Task<IReadOnlyList<ResponseRecord>> ReadResponseDocumentAsync(string projectId, CancellationToken ct) =>
        ReadAsync("Response document", async () => (await stores.ResponseDocument.ListAsync(projectId, ct))
            .Select(v => v.Value).Where(r => r.ProjectId == projectId).ToList(), ct);

    private static async Task<IReadOnlyList<T>> ReadAsync<T>(string source, Func<Task<List<T>>> read, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await read();
            }
            catch (Exception e) when (e is not OperationCanceledException && attempt < MaxAttempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt), ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                throw new SourceUnavailableException(source, e);
            }
        }
    }
}

public static class MappingSheet
{
    /// <summary>Case- and space-insensitive match of names typed in a sheet.</summary>
    public static bool Same(string? a, string? b) =>
        a is not null && b is not null && string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

    public static string Normalize(string s) => string.Join(' ', s.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    /// <summary>Rows for one PMO resource (all access levels): what a review of that resource depends on.</summary>
    public static IReadOnlyList<MappingRow> ForResource(IEnumerable<MappingRow> rows, string? resource) =>
        resource is null ? [] : rows.Where(r => Same(r.Resource, resource)).OrderBy(r => r.RowId, StringComparer.Ordinal).ToList();

    /// <summary>I1 effective dates: the row applies on <paramref name="day"/> (no dates = always).</summary>
    public static bool Effective(MappingRow r, DateOnly day) =>
        (r.EffectiveFrom is null || r.EffectiveFrom <= day) && (r.EffectiveTo is null || r.EffectiveTo >= day);

    /// <summary>
    /// v1.1: the row may be reviewed and granted on <paramref name="day"/>: not retired (F4 "Retire missing rows", rollback)
    /// and within its I1 effective dates. Retiring a row or changing its dates changes the fingerprint, so requests on it
    /// are reviewed again and never granted from it.
    /// </summary>
    public static bool Usable(MappingRow r, DateOnly day) => !r.Retired && Effective(r, day);

    /// <summary>
    /// The sheet still says what the review saw: same fingerprint for the resource, and the reviewed row still usable on
    /// <paramref name="now"/> (a row whose end date passed since the review changes no field, so the date is checked too).
    /// Every grant point asks this before it continues (approvals, the ProdApprover decision, the agent).
    /// </summary>
    public static bool AsReviewed(IReadOnlyList<MappingRow> sheet, string? resource, string sheetFingerprint, string mappingRowId, DateTimeOffset now)
    {
        if (Fingerprint(sheet, resource) != sheetFingerprint) return false;
        var row = sheet.FirstOrDefault(x => x.RowId == mappingRowId);
        return row is null || Usable(row, DateOnly.FromDateTime(now.UtcDateTime)); // a row that is gone already changed the fingerprint
    }

    /// <summary>
    /// SHA-256 over the rows for the resource (every field a review reads, fixed order). Any change to those rows,
    /// including a new duplicate or a deprecation, changes the fingerprint and sends the request back to AI review.
    /// </summary>
    public static string Fingerprint(IEnumerable<MappingRow> rows, string? resource)
    {
        var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartArray();
            foreach (var r in ForResource(rows, resource))
            {
                w.WriteStartObject();
                w.WriteString("rowId", r.RowId);
                w.WriteString("resource", Normalize(r.Resource).ToLowerInvariant());
                w.WriteString("accessLevel", Normalize(r.AccessLevel).ToLowerInvariant());
                w.WriteString("entraGroup", r.EntraGroup);
                w.WriteString("role", r.Role);
                w.WriteString("roleScope", r.RoleScope);
                w.WriteBoolean("privileged", r.Privileged);
                w.WriteBoolean("sensitive", r.SensitiveResource);
                w.WriteBoolean("deprecated", r.Deprecated);
                w.WriteString("groupState", r.GroupState.ToString());
                if (r.AzureRoleDefinitionId is not null) w.WriteString("azureRole", r.AzureRoleDefinitionId.ToLowerInvariant());
                if (r.AzureScope is not null) w.WriteString("azureScope", r.AzureScope.ToLowerInvariant());
                // Fields added on 21 Oct 2026 are written only when set, so the fingerprint of an older row is unchanged
                // (no re-review of in-flight requests just because of the deploy).
                if (r.Environment is not null) w.WriteString("environment", Normalize(r.Environment).ToLowerInvariant());
                if (r.GroupObjectId is not null) w.WriteString("groupObjectId", r.GroupObjectId.ToLowerInvariant());
                if (r.Flag != RowFlag.None) w.WriteString("flag", r.Flag.ToString());
                // v1.1 (I1) effective dates and retired, written only when set: changing them re-reviews the requests on the row.
                if (r.EffectiveFrom is not null) w.WriteString("effectiveFrom", r.EffectiveFrom.Value.ToString("O"));
                if (r.EffectiveTo is not null) w.WriteString("effectiveTo", r.EffectiveTo.Value.ToString("O"));
                if (r.Retired) w.WriteBoolean("retired", true);
                if (r.AzureRoles.Count > 0)
                {
                    w.WriteStartArray("azureRoles");
                    foreach (var g in r.AzureRoles)
                    {
                        w.WriteStartObject();
                        w.WriteString("roleName", Normalize(g.RoleName).ToLowerInvariant());
                        w.WriteString("roleId", g.RoleDefinitionId?.ToLowerInvariant());
                        w.WriteString("scope", g.Scope?.TrimEnd('/').ToLowerInvariant());
                        w.WriteString("resource", g.ResourceName?.ToLowerInvariant());
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                }
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }
        return Convert.ToHexStringLower(SHA256.HashData(buffer.ToArray()));
    }
}
