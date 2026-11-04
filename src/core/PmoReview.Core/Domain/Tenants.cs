namespace PmoReview.Core.Domain;

/// <summary>
/// Tenant separation. A project is bound to one Entra tenant; the AI agent reads and writes Entra and Azure only in its
/// own tenant (<c>AGENT_TENANT_ID</c>). For a project in another tenant the review, provisioning and verification that
/// need Entra are left to the teams working in that tenant, and the request says so.
/// </summary>
public static class Tenants
{
    public static bool Connected(Project? p, string? agentTenantId) =>
        string.IsNullOrWhiteSpace(p?.TenantId)
        || (!string.IsNullOrWhiteSpace(agentTenantId) && string.Equals(p.TenantId.Trim(), agentTenantId.Trim(), StringComparison.OrdinalIgnoreCase));

    public static string Label(Project? p) => p?.TenantName ?? p?.Name ?? "this project's tenant";

    /// <summary>Null when connected; otherwise the reason shown on checks, tasks and the trail.</summary>
    public static string? NotConnected(Project? p, string? agentTenantId) => Connected(p, agentTenantId) ? null
        : $"tenant {Label(p)} is separate from the AI agent's tenant, so the agent does not read or change it; the teams work in {Label(p)}";
}

/// <summary>Environment names as people write them (dev, tst, ppd, prd …) mapped onto the project's list.</summary>
public static class Environments
{
    private static string Key(string? e)
    {
        var k = new string((e ?? "").Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        return k switch
        {
            "dev" or "development" or "dv" => "dev",
            "test" or "tst" or "sit" or "testing" => "test",
            "uat" or "useracceptance" or "useracceptancetest" => "uat",
            "preprod" or "ppd" or "preproduction" or "pp" or "staging" => "preprod",
            "prod" or "prd" or "production" or "live" => "prod",
            _ => k,
        };
    }

    public static bool Same(string? a, string? b) => a is not null && b is not null && Key(a) == Key(b) && Key(a).Length > 0;

    /// <summary>The four environments of the group naming (dev, tst, ppd, prd tiers), offered in every project that uses environments.</summary>
    public static readonly IReadOnlyList<string> Standard = ["Dev", "Test", "PreProd", "Prod"];

    private static readonly string[] Rank = ["dev", "test", "uat", "preprod", "prod"];

    /// <summary>
    /// The environments a project offers: none when the project uses no environments (no environment field), else the
    /// standard four plus any other the project names, in flow order (the project's spelling wins). Before 4 Nov 2026 only
    /// the project's own list was offered, and the workbook import writes only the environments it found groups for, so an
    /// environment with no groups yet (e.g. Test) could not be chosen in the request form or for a new mapping row.
    /// </summary>
    public static IReadOnlyList<string> Of(Project? p)
    {
        if (p is null || p.Environments.Count == 0) return [];
        var all = p.Environments.Where(e => !string.IsNullOrWhiteSpace(e)).Select(e => e.Trim()).ToList();
        all.AddRange(Standard.Where(s => !all.Any(e => Same(e, s))));
        return all.DistinctBy(Key).Select((e, i) => (e, i))
            .OrderBy(x => Array.IndexOf(Rank, Key(x.e)) is var r && r >= 0 ? r : Rank.Length).ThenBy(x => x.i)
            .Select(x => x.e).ToList();
    }

    /// <summary>The project's spelling of the environment (from <see cref="Of"/>), or null if the project does not offer it.</summary>
    public static string? Canonical(Project? p, string? env) => Of(p).FirstOrDefault(e => Same(e, env));

    /// <summary>Prod, and any spelling that starts with prod / prd (e.g. "Prod-EU", "prd2"): unknown variants fail closed.</summary>
    public static bool IsProduction(string? env) => Key(env) is var k && (k == "prod" || k.StartsWith("prod", StringComparison.Ordinal) || k.StartsWith("prd", StringComparison.Ordinal));
}
