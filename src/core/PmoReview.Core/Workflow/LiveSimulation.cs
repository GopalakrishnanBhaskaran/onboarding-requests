using PmoReview.Core.Domain;

namespace PmoReview.Core.Workflow;

/// <summary>
/// Live simulations (Gokul, 7 Nov 2026): the developer approves every stage of a simulation, and the agent provisions,
/// assigns and verifies it in Entra and Azure for real. Because one person approves, the real writes are kept to a test
/// tenant: this rule is checked when the simulation is submitted and again before every agent write. Everything a real
/// request needs still applies on top (agent switches, allow-lists, privileged rule, never-by-agent roles, tenant separation).
/// </summary>
public static class LiveSimulation
{
    /// <summary>Null when a simulation of <paramref name="project"/> may write for real; otherwise why it stays a dry run.</summary>
    public static string? Refused(WorkflowSettings s, Project? project, string? environment, bool guest)
    {
        if (!s.SimulationLiveWrites) return "live simulations are switched off (SIMULATION_LIVE_WRITES=false)";
        if (string.Equals(s.AppEnvironment, "prod", StringComparison.OrdinalIgnoreCase)) return "live simulations are never used in production (APP_ENVIRONMENT=prod)";
        // An explicit tenant equal to the agent's: an empty tenant id counts as "connected" elsewhere, never here.
        if (string.IsNullOrWhiteSpace(project?.TenantId) || string.IsNullOrWhiteSpace(s.AgentTenantId)
            || !string.Equals(project.TenantId.Trim(), s.AgentTenantId.Trim(), StringComparison.OrdinalIgnoreCase))
            return $"tenant {Tenants.Label(project)} is not named as the AI agent's own tenant";
        if (project.IsProductionTenant != false) return $"tenant {Tenants.Label(project)} is not marked as a test (non-production) tenant";
        if (Environments.IsProduction(environment)) return $"the {environment} environment is production";
        if (guest) return "guest accounts are invited by people, never by the agent";
        return null;
    }

    /// <summary>The check before an agent write on a live simulation: null when the write may go ahead.</summary>
    public static string? RefusedNow(WorkflowSettings s, Project? project, AccessRequest r) =>
        !r.Simulated ? null
        : !r.LiveWrites ? "a dry-run simulation is never written to Entra or Azure"
        : Refused(s, project, r.Environment, r.Guest) is { } why ? $"live simulation stopped: {why}" : null;
}
