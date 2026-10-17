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
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }
        return Convert.ToHexStringLower(SHA256.HashData(buffer.ToArray()));
    }
}
