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
            "preprod" or "ppd" or "preproduction" or "pp" or "staging" => "preprod",
            "prod" or "prd" or "production" or "live" => "prod",
            _ => k,
        };
    }

    public static bool Same(string? a, string? b) => a is not null && b is not null && Key(a) == Key(b) && Key(a).Length > 0;

    /// <summary>The project's spelling of the environment, or null if the project does not have it.</summary>
    public static string? Canonical(Project? p, string? env) => p?.Environments.FirstOrDefault(e => Same(e, env));

    public static bool IsProduction(string? env) => Key(env) == "prod";
}
