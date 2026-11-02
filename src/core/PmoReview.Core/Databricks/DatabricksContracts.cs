using System.Text.RegularExpressions;
using PmoReview.Core.Domain;

namespace PmoReview.Core.Databricks;

/// <summary>A workspace the agent may set up groups in (<c>DATABRICKS_WORKSPACES</c>: name=https://adb-&lt;id&gt;.&lt;n&gt;.azuredatabricks.net).</summary>
public sealed record DatabricksWorkspace(string Name, Uri Url, long Id)
{
    private static readonly Regex Host = new(@"^adb-(\d+)\.\d+\.azuredatabricks\.net$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>"name=https://adb-123.4.azuredatabricks.net" → the workspace; anything else → null (never a free host).</summary>
    public static DatabricksWorkspace? Parse(string? entry)
    {
        var parts = entry?.Split('=', 2, StringSplitOptions.TrimEntries);
        if (parts is not [{ Length: > 0 } name, var url]) return null;
        if (!Uri.TryCreate(url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? url : "https://" + url, UriKind.Absolute, out var u)) return null;
        var m = Host.Match(u.Host);
        return m.Success && long.TryParse(m.Groups[1].Value, out var id) ? new DatabricksWorkspace(name, new Uri($"https://{u.Host.ToLowerInvariant()}/"), id) : null;
    }
}

/// <summary>
/// Databricks reads and writes for a group (v1.2, Gokul 2 Nov 2026; worker only, held only with <c>AGENT_DATABRICKS</c>).
/// Adds only: never removes an assignment, an entitlement or a privilege, never lowers ADMIN, never gives ADMIN.
/// Failures are <see cref="Directory.DirectoryWriteException"/> (writes; "…Forbidden" = the right is missing) or
/// <see cref="Directory.DirectoryReadException"/> (reads), categories only.
/// </summary>
public interface IDatabricksAccess
{
    /// <summary>A3: the Entra group in the Databricks account (automatic identity management, F111). Returns its Databricks id.</summary>
    Task<long> AddGroupToAccountAsync(string entraGroupObjectId, CancellationToken ct);

    /// <summary>The group's permissions on the workspace (USER / ADMIN); empty when it is not assigned (F112).</summary>
    Task<IReadOnlyList<string>> WorkspacePermissionsAsync(DatabricksWorkspace ws, string groupName, CancellationToken ct);

    /// <summary>A3: assigns the group to the workspace as USER (F112).</summary>
    Task AssignUserAsync(DatabricksWorkspace ws, long principalId, CancellationToken ct);

    /// <summary>The group's entitlements in the workspace; null when the workspace does not show the group (yet).</summary>
    Task<IReadOnlyList<string>?> EntitlementsAsync(DatabricksWorkspace ws, string groupName, CancellationToken ct);

    /// <summary>A4: adds entitlements to the group (F114).</summary>
    Task AddEntitlementsAsync(DatabricksWorkspace ws, string groupName, IReadOnlyList<string> entitlements, CancellationToken ct);

    /// <summary>The group's effective Unity Catalog privileges on a securable, inherited ones included (F115, F117).</summary>
    Task<IReadOnlyList<string>> EffectivePrivilegesAsync(DatabricksWorkspace ws, string securableType, string fullName, string groupName, CancellationToken ct);

    /// <summary>A5: grants privileges to the group on a securable (adds only, F117).</summary>
    Task GrantAsync(DatabricksWorkspace ws, string securableType, string fullName, string groupName, IReadOnlyList<string> privileges, CancellationToken ct);
}

/// <summary>What the agent and the verifier need for Databricks: the access, the allowed workspaces and catalogs, the policy.</summary>
public sealed record DatabricksConfig(
    IDatabricksAccess Access,
    IReadOnlyList<DatabricksWorkspace> Workspaces,
    IReadOnlyList<string> GrantCatalogs,
    DatabricksPolicy Policy)
{
    public DatabricksWorkspace? Workspace(string name) =>
        Workspaces.FirstOrDefault(w => string.Equals(w.Name, name, StringComparison.OrdinalIgnoreCase)
                                       || string.Equals(w.Url.Host, name, StringComparison.OrdinalIgnoreCase)
                                       || w.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) == name);

    public bool CatalogAllowed(string catalog) => GrantCatalogs.Contains(catalog, StringComparer.OrdinalIgnoreCase);

    /// <summary>Why the agent may not set up this target for this group, or null; the decision either way.</summary>
    public (string? Why, DatabricksWorkspace? Ws, DatabricksDecision Decision) Plan(string groupName, DatabricksTarget target)
    {
        var decision = Policy.Decide(groupName);
        var ws = Workspace(target.Workspace);
        var why = ws is null ? $"workspace {target.Workspace} is not on the agent's Databricks allow-list (DATABRICKS_WORKSPACES)"
            : !CatalogAllowed(target.Catalog) ? $"catalog {target.Catalog} is not on the agent's allow-list (DATABRICKS_GRANT_CATALOGS)"
            : !decision.ByAgent ? decision.Why
            : null;
        return (why, ws, decision);
    }
}

/// <summary>
/// The Databricks tests for a group, read live (the agent right after its set-up, the verifier at every role-team report):
/// assigned to the workspace, the entitlements its name stands for, the privileges on the catalog and on the schema.
/// </summary>
public static class DatabricksChecks
{
    public const string Workspace = "DBX_WORKSPACE";
    public const string Entitlements = "DBX_ENTITLEMENTS";
    public const string Catalog = "DBX_UC_CATALOG";
    public const string Schema = "DBX_UC_SCHEMA";

    public static async Task<IReadOnlyList<CheckResult>> RunAsync(IDatabricksAccess db, DatabricksWorkspace ws, string group, DatabricksTarget target,
        DatabricksDecision decision, CancellationToken ct)
    {
        const string G = CheckGroups.RoleTeam;
        var checks = new List<CheckResult>();
        var perms = await db.WorkspacePermissionsAsync(ws, group, ct);
        var assigned = perms.Any(p => p is "USER" or "ADMIN");
        checks.Add(new CheckResult(Workspace, G, assigned ? CheckStatus.Pass : CheckStatus.Fail, assigned
            ? $"{group} is assigned to workspace {ws.Name} ({string.Join(", ", perms)})."
            : $"{group} is not assigned to workspace {ws.Name}."));

        var have = await db.EntitlementsAsync(ws, group, ct);
        var missingEnt = decision.Entitlements.Where(e => have?.Contains(e, StringComparer.Ordinal) != true).ToList();
        checks.Add(new CheckResult(Entitlements, G, missingEnt.Count == 0 ? CheckStatus.Pass : CheckStatus.Fail, missingEnt.Count == 0
            ? $"{group} has {string.Join(", ", decision.Entitlements)} in {ws.Name} ({decision.Level})."
            : have is null ? $"Workspace {ws.Name} does not show {group}." : $"{group} lacks {string.Join(", ", missingEnt)} in {ws.Name} ({decision.Level})."));

        checks.Add(await PrivilegesAsync(db, ws, Catalog, "catalog", target.Catalog, group, decision.CatalogPrivileges, ct));
        var (type, full) = target.SchemaSecurable;
        checks.Add(await PrivilegesAsync(db, ws, Schema, type, full, group, decision.SchemaPrivileges, ct));
        return checks;
    }

    private static async Task<CheckResult> PrivilegesAsync(IDatabricksAccess db, DatabricksWorkspace ws, string code, string type, string full,
        string group, IReadOnlyList<string> wanted, CancellationToken ct)
    {
        if (wanted.Count == 0) return new CheckResult(code, CheckGroups.RoleTeam, CheckStatus.NotApplicable, $"No privilege on {type} {full} for this level.");
        var have = await db.EffectivePrivilegesAsync(ws, type, full, group, ct);
        var missing = wanted.Where(p => !have.Contains(p, StringComparer.Ordinal) && !have.Contains("ALL_PRIVILEGES", StringComparer.Ordinal)).ToList();
        return new CheckResult(code, CheckGroups.RoleTeam, missing.Count == 0 ? CheckStatus.Pass : CheckStatus.Fail, missing.Count == 0
            ? $"{group} has {string.Join(", ", wanted)} on {type} {full}."
            : $"{group} lacks {string.Join(", ", missing)} on {type} {full}.");
    }
}
