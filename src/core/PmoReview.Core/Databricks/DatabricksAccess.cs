using System.Text.Json;
using System.Text.RegularExpressions;

namespace PmoReview.Core.Databricks;

/// <summary>
/// Where a Databricks mapping row gives access: the role scope <c>dbx:&lt;workspace&gt;/&lt;catalog&gt;[.&lt;schema&gt;]</c>, e.g.
/// <c>dbx:dbw-pmo-pkt2ofpqmlf3e/sample_e2e.curated</c>. Without a schema the grants apply to the whole catalog.
/// </summary>
public sealed record DatabricksTarget(string Workspace, string Catalog, string? Schema)
{
    public const string Prefix = "dbx:";

    /// <summary>The securable the schema-level privileges go on: the schema, else the catalog (inherited by every schema).</summary>
    public (string Type, string FullName) SchemaSecurable => Schema is null ? ("catalog", Catalog) : ("schema", $"{Catalog}.{Schema}");

    public override string ToString() => $"{Prefix}{Workspace}/{Catalog}{(Schema is null ? "" : "." + Schema)}";
}

/// <summary>What the group's name decides (F114, F115; policy file <c>config/databricks-access.json</c>).</summary>
public sealed record DatabricksDecision(
    string Level,
    bool ByAgent,
    IReadOnlyList<string> Entitlements,
    IReadOnlyList<string> CatalogPrivileges,
    IReadOnlyList<string> SchemaPrivileges,
    string Why);

/// <summary>
/// Databricks access by Entra group name (Gokul, 2 Nov 2026). Pure: no I/O. The group name is split into parts at "-" and
/// "_"; each part takes the first level (in the policy's order) one of whose words it contains. An admin or owner part
/// anywhere refuses; parts naming two different levels refuse (a person decides); no level refuses. Every level gets the
/// entitlements for everyone (Workspace access) plus its own (readers: Databricks SQL access).
/// </summary>
public sealed class DatabricksPolicy
{
    /// <summary>The only entitlements the policy may give (F114). Cluster or pool creation is never automated.</summary>
    public static readonly IReadOnlySet<string> AllowedEntitlements =
        new HashSet<string>(StringComparer.Ordinal) { "workspace-access", "databricks-sql-access", "workspace-consume" };

    /// <summary>The only Unity Catalog privileges the policy may grant (F115). Never ALL_PRIVILEGES, MANAGE or ownership.</summary>
    public static readonly IReadOnlySet<string> AllowedPrivileges = new HashSet<string>(StringComparer.Ordinal)
    {
        "BROWSE", "USE_CATALOG", "USE_SCHEMA", "SELECT", "MODIFY", "CREATE_TABLE", "READ_VOLUME", "WRITE_VOLUME", "CREATE_VOLUME",
        "EXECUTE", "CREATE_FUNCTION", "CREATE_MATERIALIZED_VIEW",
    };

    private static readonly Regex Workspace = new("^[A-Za-z0-9][A-Za-z0-9_.-]{0,99}$", RegexOptions.CultureInvariant);
    private static readonly Regex Name = new("^[A-Za-z0-9_]{1,255}$", RegexOptions.CultureInvariant);

    public sealed record Level(string Name, IReadOnlyList<string> Match, bool ByAgent, IReadOnlyList<string> Entitlements,
        IReadOnlyList<string> CatalogPrivileges, IReadOnlyList<string> SchemaPrivileges, string? Why);

    public IReadOnlyList<string> EntitlementsForEveryone { get; }
    public IReadOnlyList<Level> Levels { get; }

    private DatabricksPolicy(IReadOnlyList<string> everyone, IReadOnlyList<Level> levels)
    {
        EntitlementsForEveryone = everyone;
        Levels = levels;
    }

    /// <summary>The policy shipped with the code (<c>config/databricks-access.json</c>, embedded).</summary>
    public static DatabricksPolicy Default { get; } = Parse(ReadEmbedded());

    private static string ReadEmbedded()
    {
        using var stream = typeof(DatabricksPolicy).Assembly.GetManifestResourceStream("databricks-access.json")
                           ?? throw new InvalidOperationException("Embedded Databricks access policy missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>Reads and checks a policy: an entitlement or privilege outside the allowed sets refuses the whole file.</summary>
    public static DatabricksPolicy Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        static IReadOnlyList<string> Strings(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
                ? v.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList()
                : [];
        var everyone = Strings(root, "entitlementsForEveryone");
        var levels = root.GetProperty("levels").EnumerateArray().Select(l => new Level(
            l.GetProperty("level").GetString() ?? throw new InvalidOperationException("A level has no name."),
            Strings(l, "match").Select(m => m.ToLowerInvariant()).ToList(),
            l.TryGetProperty("byAgent", out var by) && by.GetBoolean(),
            Strings(l, "entitlements"), Strings(l, "catalogPrivileges"), Strings(l, "schemaPrivileges"),
            l.TryGetProperty("why", out var why) ? why.GetString() : null)).ToList();

        var badEntitlements = everyone.Concat(levels.SelectMany(l => l.Entitlements)).Where(e => !AllowedEntitlements.Contains(e)).Distinct().ToList();
        if (badEntitlements.Count > 0) throw new InvalidOperationException($"Entitlements not allowed in the Databricks policy: {string.Join(", ", badEntitlements)}.");
        var badPrivileges = levels.SelectMany(l => l.CatalogPrivileges.Concat(l.SchemaPrivileges)).Where(p => !AllowedPrivileges.Contains(p)).Distinct().ToList();
        if (badPrivileges.Count > 0) throw new InvalidOperationException($"Privileges not allowed in the Databricks policy: {string.Join(", ", badPrivileges)}.");
        if (levels.Any(l => l.Match.Count == 0)) throw new InvalidOperationException("Every Databricks policy level needs at least one match word.");
        if (levels.Any(l => l.ByAgent && l.CatalogPrivileges.Count == 0 && l.SchemaPrivileges.Count == 0))
            throw new InvalidOperationException("A level the agent may give needs at least one privilege.");
        return new DatabricksPolicy(everyone, levels);
    }

    /// <summary>The access the group's name stands for. Never more than one level; refused when unclear.</summary>
    public DatabricksDecision Decide(string groupName)
    {
        var parts = Regex.Split(groupName.Trim().ToLowerInvariant(), "[-_ ]+").Where(p => p.Length > 0).ToList();
        Level? LevelOf(string part) => Levels.FirstOrDefault(l => l.Match.Any(part.Contains));
        var found = parts.Select(p => (Part: p, Level: LevelOf(p))).Where(x => x.Level is not null).ToList();
        var refusing = found.FirstOrDefault(x => !x.Level!.ByAgent);
        if (refusing.Level is not null)
            return Refused(refusing.Level.Name, $"the group name part '{refusing.Part}' means {refusing.Level.Name}: {refusing.Level.Why ?? "a person decides"}");
        var levels = found.Select(x => x.Level!).Distinct().ToList();
        if (levels.Count == 0)
            return Refused("Unknown", $"the group name names no access level ({string.Join(", ", Levels.Where(l => l.ByAgent).SelectMany(l => l.Match.Take(1)))} …)");
        if (levels.Count > 1)
            return Refused("Ambiguous", $"the group name names more than one access level ({string.Join(", ", levels.Select(l => l.Name))})");
        var level = levels[0];
        var entitlements = EntitlementsForEveryone.Concat(level.Entitlements).Distinct(StringComparer.Ordinal).ToList();
        return new DatabricksDecision(level.Name, true, entitlements, level.CatalogPrivileges, level.SchemaPrivileges,
            $"group name part '{found[0].Part}' means {level.Name}");
    }

    private static DatabricksDecision Refused(string level, string why) => new(level, false, [], [], [], why);

    /// <summary>The target of a Databricks row (<c>dbx:workspace/catalog[.schema]</c>), or null when the role scope names none.</summary>
    public static DatabricksTarget? ParseTarget(string? roleScope)
    {
        var s = roleScope?.Trim();
        if (string.IsNullOrEmpty(s) || !s.StartsWith(DatabricksTarget.Prefix, StringComparison.OrdinalIgnoreCase)) return null;
        var rest = s[DatabricksTarget.Prefix.Length..];
        var slash = rest.IndexOf('/');
        if (slash <= 0) return null;
        var ws = rest[..slash];
        var names = rest[(slash + 1)..].Split('.');
        if (!Workspace.IsMatch(ws) || names.Length is < 1 or > 2 || names.Any(n => !Name.IsMatch(n))) return null;
        return new DatabricksTarget(ws, names[0], names.Length == 2 ? names[1] : null);
    }

    /// <summary>True when the role scope is meant for Databricks (starts with dbx:), even if it does not parse.</summary>
    public static bool IsDatabricksScope(string? roleScope) =>
        roleScope?.TrimStart().StartsWith(DatabricksTarget.Prefix, StringComparison.OrdinalIgnoreCase) == true;
}
