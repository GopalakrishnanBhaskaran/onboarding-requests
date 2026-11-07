using PmoReview.Core.Domain;

namespace PmoReview.Core.Workflow;

/// <summary>
/// Live simulations (Gokul, 7 Nov 2026): the developer approves every stage of a simulation, and the agent provisions,
/// assigns and verifies it in Entra and Azure for real. Because one person approves, the real writes are kept to a test
/// tenant and to accounts the agent creates for the simulation: this rule is checked when the simulation is submitted and
/// again before every agent write. Everything a real request needs still applies on top (agent switches, allow-lists,
/// privileged rule, never-by-agent roles, tenant separation). The agent issues a TAP for a live simulation's new account only
/// with <c>SIMULATION_LIVE_TAP</c> (Gokul, 7 Nov 2026, approval-gated: the address was approved by the developer alone).
/// </summary>
public static class LiveSimulation
{
    /// <summary>
    /// Null when a simulation of <paramref name="project"/> may write for real; otherwise why it stays a dry run.
    /// <paramref name="allProjects"/>: every project, so a tenant another project calls production (or never marked) is never
    /// written to by a simulation (independent review 7 Nov 2026, finding 7).
    /// </summary>
    public static string? Refused(WorkflowSettings s, Project? project, string? environment, bool guest, IEnumerable<Project> allProjects)
    {
        if (!s.SimulationLiveWrites) return "live simulations are switched off (SIMULATION_LIVE_WRITES=false)";
        if (string.Equals(s.AppEnvironment, "prod", StringComparison.OrdinalIgnoreCase)) return "live simulations are never used in production (APP_ENVIRONMENT=prod)";
        // An explicit tenant equal to the agent's: an empty tenant id counts as "connected" elsewhere, never here.
        if (string.IsNullOrWhiteSpace(project?.TenantId) || string.IsNullOrWhiteSpace(s.AgentTenantId) || !Same(project.TenantId, s.AgentTenantId))
            return $"tenant {Tenants.Label(project)} is not named as the AI agent's own tenant";
        if (project.IsProductionTenant != false) return $"tenant {Tenants.Label(project)} is not marked as a test (non-production) tenant";
        if (allProjects.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p.TenantId) && Same(p.TenantId, project.TenantId) && p.IsProductionTenant != false) is { } other)
            return $"project {other.ProjectId} uses the same tenant and is not marked as a test tenant";
        // Gokul, 7 Nov 2026 ("Yes, Prod too (test tenant)"): every environment of a test tenant, Prod included (review finding 3
        // closed that path; the owner reopened it). The tenant checks above keep it to a tenant marked not production.
        if (guest) return "guest accounts are invited by people, never by the agent";
        return null;
    }

    /// <summary>
    /// The check before an agent write on a simulation: null when the write may go ahead. <paramref name="row"/>: the mapping
    /// row (kept for the call sites; since 7 Nov 2026 a Prod row or a row for every environment is written in a test tenant too).
    /// </summary>
    public static string? RefusedNow(WorkflowSettings s, Project? project, AccessRequest r, MappingRow? row, IEnumerable<Project> allProjects)
    {
        if (!r.Simulated) return null;
        if (!r.LiveWrites) return "a dry-run simulation is never written to Entra or Azure";
        _ = row;
        var why = Refused(s, project, r.Environment, r.Guest, allProjects);
        return why is null ? null : $"live simulation stopped: {why}";
    }

    private static bool Same(string? a, string? b) =>
        a is not null && b is not null && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
}
