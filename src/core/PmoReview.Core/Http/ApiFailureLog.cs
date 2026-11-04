using System.Text.RegularExpressions;
using PmoReview.Core.Security;
using PmoReview.Core.Storage;
using PmoReview.Core.Workflow;

namespace PmoReview.Core.Http;

/// <summary>
/// Where an unexpected API error can be found again (re-check 6, F114). On SWA managed Functions, runtime errors are visible
/// only through Application Insights, which the pilot never creates (CLAUDE.md rule 2). The "Reference" a person quotes
/// therefore led nowhere. Each unexpected error now goes on the Issues list (Issues and reports; also
/// <c>tools/workflow_diag.sh</c>): one issue per route and error type, counted, with the last references, the error type
/// and the code location. Never the error message, the request body, the query or the caller: those can hold personal or
/// secret data. Recording never fails the response.
/// </summary>
public sealed partial class ApiFailureLog(DataStores stores, IssueLog issues)
{
    /// <summary>Issues of a caller with no project (e.g. their access rows could not be read): read with workflow_diag.sh.</summary>
    public const string PlatformPartition = "_platform";
    public const int KeepReferences = 5;
    private const int Frames = 3;

    /// <param name="template">The route template (never the path: it can carry an Enterprise ID).</param>
    /// <param name="projectIds">Projects named by the route or the query; recorded only in those the caller has a role in.</param>
    public async Task RecordAsync(string method, string? template, Exception e, string correlationId, CallerContext? caller,
        IEnumerable<string?> projectIds, CancellationToken ct)
    {
        var route = $"{method.ToUpperInvariant()} {template ?? "(no matching route)"}";
        var type = e.GetType().Name;
        var step = $"api:{route}:{type}";
        var targets = projectIds.Where(p => !string.IsNullOrWhiteSpace(p) && caller?.InProject(p!) == true).Select(p => p!).Distinct(StringComparer.Ordinal).ToList();
        if (targets.Count == 0 && caller is not null) targets.AddRange(caller.Grants.Keys.Order(StringComparer.Ordinal));
        if (targets.Count == 0) targets.Add(PlatformPartition);
        foreach (var projectId in targets)
        {
            var existing = await stores.Issues.GetAsync(projectId, Issue.MakeId(null, step, IssueKind.NeedsDeveloper), ct);
            var references = References(existing?.Value.Detail).Prepend(correlationId).Distinct(StringComparer.Ordinal).Take(KeepReferences);
            await issues.RecordAsync(projectId, null, step, IssueKind.NeedsDeveloper, IssueSeverity.Warning,
                $"The website's API failed on {route} ({type}): the person saw \"Something went wrong\" with reference {correlationId}.",
                $"References, latest first: {string.Join(", ", references)}. Error: {Describe(e)}. No request data is kept.", ct);
        }
    }

    /// <summary>The error type, its inner error's type and the first frames in this code (type, method, file and line only).</summary>
    public static string Describe(Exception e)
    {
        var frames = (e.StackTrace ?? "").Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("at PmoReview.", StringComparison.Ordinal))
            .Take(Frames).Select(Frame).ToList();
        return $"{e.GetType().Name}{(e.InnerException is { } inner ? $" (inner {inner.GetType().Name})" : "")}" +
               (frames.Count > 0 ? $" at {string.Join(" ← ", frames)}" : " (no frame in this code)");
    }

    /// <summary>"at PmoReview.X.Y(String id) in /src/…/Y.cs:line 42" → "PmoReview.X.Y (Y.cs:42)": parameter names and paths dropped.</summary>
    private static string Frame(string line)
    {
        var m = FrameLine().Match(line);
        if (!m.Success) return line[3..].Split('(')[0];
        return m.Groups["file"].Success ? $"{m.Groups["method"].Value} ({Path.GetFileName(m.Groups["file"].Value)}:{m.Groups["line"].Value})" : m.Groups["method"].Value;
    }

    private static IEnumerable<string> References(string? detail)
    {
        var m = ReferenceList().Match(detail ?? "");
        return m.Success ? m.Groups[1].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) : [];
    }

    [GeneratedRegex(@"^at (?<method>[^(]+)\([^)]*\)(?: in (?<file>.+):line (?<line>\d+))?")]
    private static partial Regex FrameLine();

    [GeneratedRegex(@"References, latest first: ([^.]*)\.")]
    private static partial Regex ReferenceList();
}
