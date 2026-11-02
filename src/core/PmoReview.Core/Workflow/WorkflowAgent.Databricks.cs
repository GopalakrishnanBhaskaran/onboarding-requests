using PmoReview.Core.Databricks;
using PmoReview.Core.Directory;
using PmoReview.Core.Domain;
using PmoReview.Core.Storage;

namespace PmoReview.Core.Workflow;

/// <summary>
/// v1.2 (Gokul, 2 Nov 2026; <c>AGENT_DATABRICKS</c>, off until approved): the Databricks steps A3–A5 of a request's group,
/// done by the agent at With Role Team — reached only after the named Owner and then Security approved, both re-checked
/// before any write (<see cref="AssignRoleAsync"/>). For a mapping row whose role scope is
/// <c>dbx:&lt;workspace&gt;/&lt;catalog&gt;[.&lt;schema&gt;]</c>: the group is added to the Databricks account (automatic identity
/// management) and assigned to the workspace as USER (A3); it gets the entitlements its name stands for — Workspace access
/// for everyone, Databricks SQL access for readers (A4); and the Unity Catalog privileges of that level on the catalog and
/// schema (A5). Then the agent tests all of it live and records the result in the request's trail, its checks and the
/// audit log. Adds only; never ADMIN, never an admin or owner group, never a workspace or catalog off its allow-list.
/// </summary>
public sealed partial class WorkflowAgent
{
    private const string DatabricksOff =
        "the agent's Databricks set-up is off: the role assignment team sets the group up in Databricks (workspace, entitlements, Unity Catalog)";
    private const string DatabricksMixed =
        "the Databricks row also carries Azure roles: one row per plane (put the Azure roles on a row and group of their own)";

    private async Task<Step?> DatabricksSetupAsync(AccessRequest r, TeamTask task, ReviewedScope scope, AgentWork work, Project? project,
        DateTimeOffset now, CancellationToken ct)
    {
        var flow = r.Flow!;
        var group = scope.EntraGroup;
        var target = DatabricksPolicy.ParseTarget(scope.RoleScope);
        if (target is null)
            return ToRoleTeam(r, task, work with { Manual = $"role scope {scope.RoleScope} is not dbx:<workspace>/<catalog>[.<schema>]" }, project, now, IssueSeverity.High);
        var (why, ws, decision) = databricks!.Plan(group, target);
        if (why is not null)
            return ToRoleTeam(r, task, work with { Manual = $"Databricks: {why}" }, project, now, decision.ByAgent ? IssueSeverity.High : IssueSeverity.Info);

        var db = databricks.Access;
        var done = new List<string>();
        IReadOnlyList<CheckResult> checks;
        try
        {
            var groupId = flow.IdTeamTask?.Agent?.GroupObjectId ?? flow.IdTeamTask?.IdReport?.GroupObjectId;
            if (groupId is null)
            {
                var found = await directory!.FindGroupsAsync(group, ct);
                if (found.Count != 1)
                    return ToRoleTeam(r, task, work with { Manual = $"group {group} is not unique in Entra ({found.Count} found)" }, project, now, IssueSeverity.High);
                groupId = found[0].ObjectId;
            }

            // A3: in the account (AIM) and on the workspace as USER. Reads before every write; a repeat changes nothing.
            var principal = await db.AddGroupToAccountAsync(groupId, ct);
            if (!(await db.WorkspacePermissionsAsync(ws!, group, ct)).Any(p => p is "USER" or "ADMIN"))
            {
                await db.AssignUserAsync(ws!, principal, ct);
                done.Add($"assigned to workspace {ws!.Name} as USER");
            }

            // A4: the entitlements the name stands for. A workspace that does not show the group yet is retried next run.
            var have = await db.EntitlementsAsync(ws!, group, ct);
            if (have is null) return RetryOrTeam(r, task, work, $"databricks:{task.Id}", "DatabricksGroupNotInWorkspaceYet", project, now, isIdTeam: false);
            var entitlements = decision.Entitlements.Where(e => !have.Contains(e, StringComparer.Ordinal)).ToList();
            if (entitlements.Count > 0)
            {
                await db.AddEntitlementsAsync(ws!, group, entitlements, ct);
                done.Add($"entitlements {string.Join(", ", entitlements)}");
            }

            // A5: Unity Catalog privileges on the catalog, then on the schema (or the whole catalog when no schema is named).
            var (schemaType, schemaName) = target.SchemaSecurable;
            foreach (var (type, name, wanted) in new[] { ("catalog", target.Catalog, decision.CatalogPrivileges), (schemaType, schemaName, decision.SchemaPrivileges) })
            {
                if (wanted.Count == 0) continue;
                var effective = await db.EffectivePrivilegesAsync(ws!, type, name, group, ct);
                var add = wanted.Where(p => !effective.Contains(p, StringComparer.Ordinal) && !effective.Contains("ALL_PRIVILEGES", StringComparer.Ordinal)).ToList();
                if (add.Count == 0) continue;
                await db.GrantAsync(ws!, type, name, group, add, ct);
                done.Add($"{string.Join(", ", add)} on {type} {name}");
            }

            // The tests: everything read back live.
            checks = await DatabricksChecks.RunAsync(db, ws!, group, target, decision, ct);
        }
        catch (DirectoryWriteException e) when (e.Forbidden)
        {
            return RefusedOrTeam(r, task, work, e.Category, project, now, isIdTeam: false);
        }
        catch (DirectoryReadException e)
        {
            return RetryOrTeam(r, task, work, $"databricks:{task.Id}", e.Category, project, now, isIdTeam: false);
        }

        var summary = done.Count == 0 ? "everything was already in place" : string.Join("; ", done);
        var failed = checks.Where(c => c.Status == CheckStatus.Fail).ToList();
        var tests = $"{checks.Count(c => c.Status == CheckStatus.Pass)} of {checks.Count(c => c.Status != CheckStatus.NotApplicable)} tests passed";
        Func<CancellationToken, Task> audit = c => stores.AuditEvents.CreateAsync(new AuditEvent
        {
            Id = AuditEvent.NewId(now), ProjectId = r.ProjectId, At = now, Actor = Agent, Action = "databricks-setup", Target = $"group {group}",
            Details = $"request {r.Id}; {target}; level {decision.Level}; {summary}; {tests}: {string.Join(" · ", checks.Select(x => $"{x.Code} {x.Status}"))}",
            Result = failed.Count == 0 ? "Passed" : "Failed",
        }, c);

        if (failed.Count > 0)
        {
            var left = ToRoleTeam(r, task with { Checks = checks },
                work with { Manual = $"Databricks set-up did not verify ({tests}): {string.Join(" ", failed.Select(f => f.Detail))}" }, project, now, IssueSeverity.High);
            left.Effects.Add(audit);
            return left;
        }

        var roles = ExpectedRoles(scope);
        await UpdateResponseAsync(r, x => x with { AssignedRoles = roles, State = ResponseState.Assigned, AssignedBy = AgentName, AssignedAt = now }, ct);
        var reported = task with
        {
            Agent = work with { At = now, RoleAssigned = true, Completed = true },
            State = TeamTaskState.Reported, ReportedAt = now, ReportedBy = AgentName, Checks = checks,
            PickedUpAt = task.PickedUpAt ?? now, PickedUpBy = task.PickedUpBy ?? AgentName,
            RoleReport = new RoleTeamReport
            {
                AssignedRoles = roles,
                Notes = $"Set up in Databricks by the AI agent for group {group} ({decision.Level}: {decision.Why}): {summary}. {tests}.",
            },
        };
        var next = (flow with { RoleTeamTask = reported }).Waiting(WaitingOn.Agent, now)
            .Log(now, Agent, "agent-databricks-setup",
                $"AI agent set up group {group} in Databricks workspace {ws!.Name} for {target} as {decision.Level} ({decision.Why}): {summary}. " +
                $"Entitlements {string.Join(", ", decision.Entitlements)}; catalog {string.Join(", ", decision.CatalogPrivileges)}; " +
                $"{target.SchemaSecurable.Type} {string.Join(", ", decision.SchemaPrivileges)}.")
            .Log(now, Agent, "agent-databricks-tests", $"{tests}: {string.Join(" · ", checks.Select(x => $"{x.Code} {x.Status}"))}.");
        var effects = new List<Func<CancellationToken, Task>> { audit };
        if (work.RefusedSince is not null) effects.Add(c => issues.ResolveAsync(r.ProjectId, r.Id, [IssueKind.DependencyFailure], c));
        if (task.Agent?.Manual is not null) effects.Add(c => issues.ResolveAsync(r.ProjectId, r.Id, [IssueKind.NeedsDeveloper], c));
        return new Step(r with { Flow = next }, effects);
    }
}
