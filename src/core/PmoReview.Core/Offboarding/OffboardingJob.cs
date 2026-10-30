using Microsoft.Extensions.Logging;
using PmoReview.Core.Common;
using PmoReview.Core.Directory;
using PmoReview.Core.Domain;
using PmoReview.Core.Storage;
using PmoReview.Core.Workflow;

namespace PmoReview.Core.Offboarding;

/// <summary>
/// F5 (v1.1): the worker side of offboarding. Plan: read Entra and Azure and list every change (dry run). Execute (only a
/// confirmed plan, only with <c>AGENT_OFFBOARDING</c>): revoke sign-in sessions → remove the TAP → remove group memberships →
/// remove Azure role assignments → disable the account (→ soft-delete only with <c>OFFBOARD_DELETE_USER</c>) → mark the
/// person a leaver → close the person's open requests. Every step reads before it writes, so a repeat changes nothing
/// twice; progress is saved after each step. Verify: read everything again. Nothing left → Completed and one mail to the
/// project's PMO only; anything left → PartiallyCompleted, an escalation, and no completion mail.
/// </summary>
/// <param name="writer">Entra writes; null unless <c>AGENT_OFFBOARDING</c> (plans are still made).</param>
/// <param name="remover">Azure role assignment deletes; null unless <c>AGENT_OFFBOARDING</c>.</param>
public sealed class OffboardingJob(DataStores stores, IDirectoryReader directory, IOffboardingReader reader, IRoleAssignmentLister? rbac, IOffboardingWriter? writer,
    IRoleAssignmentRemover? remover, Notifier notifier, IssueLog issues, IClock clock, WorkflowSettings settings, ILogger<OffboardingJob> logger)
{
    public const string Actor = WorkflowAgent.AgentName;
    public const string Resolve = "Resolve user", Revoke = "Revoke sign-in sessions", Tap = "Remove Temporary Access Pass", Membership = "Remove group membership",
        ReadRbac = "Read Azure role assignments", Rbac = "Remove Azure role assignment", Disable = "Disable account", Delete = "Delete account",
        Leaver = "Mark leaver in people inventory", Close = "Close open request";
    private const int MaxAttempts = 3;
    private const string ByPeople = "left to people: the account holds an Entra directory role, so the AI agent changes nothing in Entra or Azure for it";

    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var handled = 0;
        foreach (var project in (await stores.Projects.ListAllAsync(ct)).Select(p => p.Value))
        {
            if (!Tenants.Connected(project, settings.AgentTenantId)) continue; // the agent reads and writes only its own tenant
            foreach (var v in (await stores.OffboardingRuns.ListAsync(project.ProjectId, ct)).Where(r => r.Value.Status is OffboardingStatus.Planning or OffboardingStatus.Executing))
            {
                try
                {
                    // Belt and braces before anything is read or changed: the API checks this before it records a run and again
                    // at the confirmation. A confirmed run is never stopped half-way by it (it goes through verify instead).
                    if (v.Value.Status == OffboardingStatus.Planning
                        && await OffboardingService.NotOffboardableAsync(stores, project, v.Value.EnterpriseId, settings, ct) is { } why)
                    {
                        await SaveAsync(project, v, v.Value with
                        {
                            Status = OffboardingStatus.Failed, FinishedAt = clock.UtcNow, UpdatedAt = clock.UtcNow, Leftovers = [$"{why.Reason}: nothing was changed"],
                        }, "offboarding-failed", why.Reason, ct);
                        continue;
                    }
                    if (v.Value.Status == OffboardingStatus.Planning) await PlanAsync(project, v, ct);
                    else await ExecuteAsync(project, v, ct);
                    handled++;
                }
                catch (ConcurrencyException)
                {
                    // Another worker instance moved this run on; the next run looks again.
                }
            }
        }
        return handled;
    }

    // ---- plan (dry run: reads only) ---------------------------------------------------------------------------------

    private async Task PlanAsync(Project project, Versioned<OffboardingRun> v, CancellationToken ct)
    {
        var run = v.Value;
        var now = clock.UtcNow;
        DirectoryUser? user;
        List<OffboardingStep> steps;
        try
        {
            user = await directory.FindUserAsync(run.EnterpriseId, ct);
            if (user is null)
            {
                await SaveAsync(project, v, run with
                {
                    Status = OffboardingStatus.Failed, FinishedAt = now, UpdatedAt = now,
                    Steps = [Step(Resolve, run.EnterpriseId, "find the Entra user by Enterprise ID (UPN)", OffboardingStepResult.Failed, "not found in Entra: nothing was planned or changed")],
                }, "offboarding-failed", "user not found", ct);
                return;
            }
            steps = await PlanStepsAsync(project, run, user, ct);
        }
        catch (DirectoryReadException e)
        {
            await TransientAsync(project, v, "plan", e.Category, ct);
            return;
        }
        var planned = run with
        {
            ObjectId = user.ObjectId, Status = OffboardingStatus.Planned, Steps = steps, PlanHash = OffboardingService.PlanHash(steps), PlannedAt = now, UpdatedAt = now,
            NotCovered = user.IsGuest ? [.. run.NotCovered, "Sessions of a guest (external user) in their home tenant: Entra revokes only this tenant's sessions (F71)"] : run.NotCovered,
            Attempts = new Dictionary<string, int>(),
        };
        await SaveAsync(project, v, planned, "offboarding-planned", $"{steps.Count(s => s.Result == OffboardingStepResult.Planned)} change(s) planned, plan {planned.PlanHash}", ct);
    }

    private async Task<List<OffboardingStep>> PlanStepsAsync(Project project, OffboardingRun run, DirectoryUser user, CancellationToken ct)
    {
        var id = user.ObjectId;
        var steps = new List<OffboardingStep> { Step(Resolve, run.EnterpriseId, "find the Entra user by Enterprise ID (UPN)", OffboardingStepResult.Done, $"object id {id}") };
        var memberships = await reader.MembershipsAsync(id, ct);
        // An account that holds an Entra directory role (directly or through a role-assignable group) is offboarded by
        // people: the AI agent plans no Entra or Azure change for it (PERMISSIONS.md row C); verify then lists what is left.
        var roleHolder = memberships.Any(m => m.DirectoryRole);
        OffboardingStep Write(string name, string target, string action, string detail) =>
            roleHolder ? Step(name, target, action, OffboardingStepResult.Skipped, $"{detail}: {ByPeople}") : Step(name, target, action, OffboardingStepResult.Planned, detail);
        steps.Add(user.IsGuest
            ? Step(Revoke, id, "POST /users/{id}/revokeSignInSessions", OffboardingStepResult.Skipped, "guest account: its home tenant ends its sessions (F71)")
            : Write(Revoke, id, "POST /users/{id}/revokeSignInSessions", "every refresh token and session cookie; takes a few minutes (F71)"));
        foreach (var tap in await reader.TapMethodIdsAsync(id, ct))
            steps.Add(Write(Tap, tap, "DELETE the user's Temporary Access Pass method", "Temporary Access Pass method"));
        foreach (var m in memberships.Where(m => m.Direct || m.NotChangeable is not null).OrderBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase))
            steps.Add(m.NotChangeable is null
                ? Write(Membership, m.GroupObjectId, "DELETE /groups/{group}/members/{id}/$ref", m.DisplayName)
                : Step(Membership, m.GroupObjectId, "left to people", OffboardingStepResult.Skipped, $"{m.DisplayName}: {m.NotChangeable}"));
        var (assignments, unreadable) = await AssignmentsAsync(id, ct);
        foreach (var s in unreadable)
            steps.Add(Step(ReadRbac, $"/subscriptions/{s}", "GET role assignments", OffboardingStepResult.Skipped, "the AI agent may not read role assignments in this subscription"));
        foreach (var a in assignments)
            steps.Add(Write(Rbac, a.Id, "DELETE role assignment", $"{BuiltInRoles.NameOf(a.RoleDefinitionId) ?? a.RoleDefinitionId} at {a.Scope}"));
        steps.Add(user.AccountEnabled
            ? Write(Disable, id, "PATCH /users/{id} accountEnabled=false", "sign-in blocked")
            : Step(Disable, id, "PATCH /users/{id} accountEnabled=false", OffboardingStepResult.Skipped, "already disabled"));
        steps.Add(settings.OffboardDeleteUser
            ? Write(Delete, id, "DELETE /users/{id}", "soft delete: restorable for 30 days (F75)")
            : Step(Delete, id, "DELETE /users/{id}", OffboardingStepResult.Skipped, "off (OFFBOARD_DELETE_USER=false): the account stays, disabled"));
        var person = await stores.PeopleInventory.GetAsync(project.ProjectId, Person.MakeId(run.EnterpriseId), ct);
        steps.Add(person is null
            ? Step(Leaver, run.EnterpriseId, "people inventory Status=Leaver", OffboardingStepResult.Skipped, "not in the people inventory")
            : Step(Leaver, person.Value.Id, "people inventory Status=Leaver", person.Value.Status == PersonStatus.Leaver ? OffboardingStepResult.Skipped : OffboardingStepResult.Planned,
                person.Value.Status == PersonStatus.Leaver ? "already a leaver" : person.Value.DisplayName));
        foreach (var r in (await stores.Requests.ListAsync(project.ProjectId, ct)).Select(x => x.Value)
                     .Where(r => r.Flow is { IsFinal: false } && string.Equals(r.DomainId, run.EnterpriseId, StringComparison.OrdinalIgnoreCase)))
            steps.Add(Step(Close, r.Id, "request status Closed – offboarded", OffboardingStepResult.Planned, $"{r.AccessLevel} on {r.Resource} ({StatusText.Label(r.Flow!.Status)})"));
        return steps;
    }

    /// <summary>The user's own role assignments in every subscription in scope (deduplicated: one above a subscription shows in each).</summary>
    private async Task<(List<RoleAssignmentRef> Assignments, List<string> Unreadable)> AssignmentsAsync(string userObjectId, CancellationToken ct)
    {
        var found = new Dictionary<string, RoleAssignmentRef>(StringComparer.OrdinalIgnoreCase);
        var unreadable = new List<string>();
        if (rbac is null) return ([], []);
        var subscriptions = settings.OffboardSubscriptions.Count > 0 ? settings.OffboardSubscriptions : await rbac.SubscriptionsAsync(ct);
        foreach (var s in subscriptions)
        {
            var list = await rbac.AssignmentsInSubscriptionAsync(s, userObjectId, ct);
            if (list is null) { unreadable.Add(s); continue; }
            foreach (var a in list.Where(a => string.Equals(a.PrincipalId, userObjectId, StringComparison.OrdinalIgnoreCase))) found.TryAdd(a.Id, a);
        }
        return (found.Values.OrderBy(a => a.Scope, StringComparer.OrdinalIgnoreCase).ToList(), unreadable);
    }

    // ---- execute (a confirmed plan only) -----------------------------------------------------------------------------

    private async Task ExecuteAsync(Project project, Versioned<OffboardingRun> v, CancellationToken ct)
    {
        var run = v.Value;
        var now = clock.UtcNow;
        if (writer is null || remover is null || !settings.AgentOffboarding)
        {
            var done = run.Steps.Count(s => s.Result == OffboardingStepResult.Done && s.Name != Resolve);
            if (done == 0)
            {
                await SaveAsync(project, v, run with { Status = OffboardingStatus.Failed, FinishedAt = now, UpdatedAt = now, Leftovers = ["offboarding was switched off (AGENT_OFFBOARDING) before the plan ran: nothing was changed"] },
                    "offboarding-failed", "switched off", ct);
                return;
            }
            // Switched off half-way: what is done stays done; the rest is left to people, and verify reports it (escalation, no mail).
            var rest = run.Steps.Select(s => s.Result == OffboardingStepResult.Planned
                ? s with { Result = OffboardingStepResult.Skipped, Detail = $"{s.Detail}: left to people (AGENT_OFFBOARDING was switched off after {done} change(s))" } : s).ToList();
            v = await stores.OffboardingRuns.ReplaceAsync(run with { Steps = rest, UpdatedAt = now }, v.ETag, ct);
            await AuditAsync(project, "offboarding-switched-off", run.EnterpriseId, $"run {run.Id}: switched off after {done} change(s); the rest is left to people", run.CorrelationId, ct);
            await VerifyAsync(project, v, ct);
            return;
        }
        if (run.PlanHash != OffboardingService.PlanHash(run.Steps))
        {
            await SaveAsync(project, v, run with { Status = OffboardingStatus.Failed, FinishedAt = now, UpdatedAt = now, Leftovers = ["the plan changed after it was confirmed: nothing was changed; make a new plan"] },
                "offboarding-failed", "plan hash mismatch", ct);
            return;
        }

        DirectoryUser? user;
        IReadOnlyList<GroupMembership> memberships;
        IReadOnlyList<string> taps;
        try
        {
            user = await directory.FindUserAsync(run.EnterpriseId, ct);
            var deleted = user is null && run.Steps.Any(s => s.Name == Delete && s.Result == OffboardingStepResult.Done);
            if (!deleted && (user is null || user.ObjectId != run.ObjectId))
            {
                await SaveAsync(project, v, run with { Status = OffboardingStatus.Failed, FinishedAt = now, UpdatedAt = now, Leftovers = [$"{run.EnterpriseId} is no longer the account that was planned: nothing more was changed; make a new plan"] },
                    "offboarding-failed", "account changed", ct);
                return;
            }
            memberships = deleted ? [] : await reader.MembershipsAsync(run.ObjectId!, ct);
            taps = deleted ? [] : await reader.TapMethodIdsAsync(run.ObjectId!, ct);
        }
        catch (DirectoryReadException e)
        {
            await TransientAsync(project, v, "execute", e.Category, ct);
            return;
        }

        var steps = run.Steps.ToList();
        var attempts = new Dictionary<string, int>(run.Attempts);
        // The account got an Entra directory role after the plan: no more Entra or Azure writes; people offboard it (verify lists it).
        var roleHolder = memberships.Any(m => m.DirectoryRole);
        for (var i = 0; i < steps.Count; i++)
        {
            var s = steps[i];
            if (s.Result != OffboardingStepResult.Planned) continue;
            if (roleHolder && s.Name is Revoke or Tap or Membership or Rbac or Disable or Delete)
            {
                steps[i] = s with { Result = OffboardingStepResult.Skipped, Detail = $"{s.Detail}: {ByPeople}" };
                await AuditAsync(project, "offboarding-step", s.Target, $"run {run.Id}: {s.Name} → Skipped (directory role found at execute)", run.CorrelationId, ct);
                v = await stores.OffboardingRuns.ReplaceAsync(run with { Steps = steps, Attempts = attempts, UpdatedAt = clock.UtcNow }, v.ETag, ct);
                run = v.Value;
                continue;
            }
            string detail;
            try
            {
                detail = await DoAsync(project, run, s, user, memberships, taps, ct);
                steps[i] = s with { Result = OffboardingStepResult.Done, Detail = detail };
            }
            catch (DirectoryReadException e) when (e is not DirectoryWriteException { Forbidden: true } && e.Category is "GraphUnavailable" or "ArmUnavailable" or "NotReplicated" or "IdentityUnavailable" or "Throttled")
            {
                var key = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                attempts[key] = attempts.GetValueOrDefault(key) + 1;
                if (attempts[key] < MaxAttempts)
                {
                    // Keep what is done; this step and the rest run on the next worker run (each re-reads before it writes).
                    await SaveAsync(project, v, run with { Steps = steps, Attempts = attempts, UpdatedAt = clock.UtcNow }, "offboarding-step",
                        $"{s.Name} {s.Target}: paused ({e.Category}), retried on the next run", ct);
                    return;
                }
                steps[i] = s with { Result = OffboardingStepResult.Failed, Detail = $"{s.Detail}: failed {MaxAttempts} times ({e.Category})" };
            }
            catch (DirectoryReadException e)
            {
                steps[i] = s with { Result = OffboardingStepResult.Failed, Detail = $"{s.Detail}: refused ({e.Category})" };
            }
            catch (ConcurrencyException)
            {
                steps[i] = s with { Result = OffboardingStepResult.Failed, Detail = $"{s.Detail}: changed by someone else meanwhile" };
            }
            await AuditAsync(project, "offboarding-step", s.Target, $"run {run.Id}: {s.Name} → {steps[i].Result} ({steps[i].Detail})", run.CorrelationId, ct);
            // Progress is saved after every write, so a crash or a lost save never repeats a step blindly.
            v = await stores.OffboardingRuns.ReplaceAsync(run with { Steps = steps, Attempts = attempts, UpdatedAt = clock.UtcNow }, v.ETag, ct);
            run = v.Value;
        }
        await VerifyAsync(project, v, ct);
    }

    /// <summary>One write, after its read. Returns the detail recorded for the step.</summary>
    private async Task<string> DoAsync(Project project, OffboardingRun run, OffboardingStep s, DirectoryUser? user, IReadOnlyList<GroupMembership> memberships,
        IReadOnlyList<string> taps, CancellationToken ct)
    {
        var id = run.ObjectId!;
        switch (s.Name)
        {
            case Revoke:
                await writer!.RevokeSessionsAsync(id, ct);
                return "every session revoked (takes a few minutes to apply)";
            case Tap:
                if (!taps.Contains(s.Target)) return "already removed";
                await writer!.DeleteTapAsync(id, s.Target, ct);
                return "removed";
            case Membership:
                if (!memberships.Any(m => m.GroupObjectId == s.Target && m.Direct)) return $"{s.Detail}: already removed";
                await writer!.RemoveMemberAsync(s.Target, id, ct);
                return $"{s.Detail}: removed";
            case Rbac:
                var a = (await AssignmentsAsync(id, ct)).Assignments.FirstOrDefault(x => string.Equals(x.Id, s.Target, StringComparison.OrdinalIgnoreCase));
                if (a is null) return $"{s.Detail}: already removed";
                await remover!.DeleteAsync(a, id, ct);
                return $"{s.Detail}: removed";
            case Disable:
                if (user is { AccountEnabled: false }) return "already disabled";
                await writer!.DisableAsync(id, ct);
                return "disabled";
            case Delete:
                if (user is null) return "already deleted";
                await writer!.DeleteUserAsync(id, ct);
                return "soft-deleted: restorable for 30 days (F75)";
            case Leaver:
                var p = await stores.PeopleInventory.GetAsync(project.ProjectId, s.Target, ct);
                if (p is null) return "not in the people inventory";
                if (p.Value.Status == PersonStatus.Leaver) return "already a leaver";
                await stores.PeopleInventory.ReplaceAsync(p.Value with { Status = PersonStatus.Leaver, Version = p.Value.Version + 1, UpdatedBy = Actor, UpdatedAt = clock.UtcNow }, p.ETag, ct);
                return "Status=Leaver";
            case Close:
                var r = await stores.Requests.GetAsync(project.ProjectId, s.Target, ct);
                if (r?.Value.Flow is not { } flow) return "request not found";
                if (flow.IsFinal) return $"already {StatusText.Label(flow.Status)}";
                var now = clock.UtcNow;
                await stores.Requests.ReplaceAsync(r.Value with
                {
                    Flow = flow.To(RequestStatus.Closed, WaitingOn.None, now, Actor, "offboarded",
                        $"Closed: {run.EnterpriseId} was offboarded (run {run.Id}, confirmed by {run.ConfirmedByName ?? run.ConfirmedBy}). Nothing more is granted for this request."),
                }, r.ETag, ct);
                return "closed";
            default:
                throw new DirectoryWriteException("WriteRefused");
        }
    }

    // ---- verify and report --------------------------------------------------------------------------------------------

    private async Task VerifyAsync(Project project, Versioned<OffboardingRun> v, CancellationToken ct)
    {
        var run = v.Value;
        var leftovers = new List<string>();
        try
        {
            var user = await directory.FindUserAsync(run.EnterpriseId, ct);
            var deleteDone = run.Steps.Any(s => s.Name == Delete && s.Result == OffboardingStepResult.Done);
            if (user is not null && user.ObjectId == run.ObjectId)
            {
                if (deleteDone) leftovers.Add("the account still exists although it was deleted (replication can lag; look again)");
                if (user.AccountEnabled) leftovers.Add("the account is still enabled");
                foreach (var m in await reader.MembershipsAsync(run.ObjectId!, ct))
                    if (m.Direct || m.NotChangeable is not null)
                        leftovers.Add($"still in {m.DisplayName}{(m.NotChangeable is null ? "" : $" ({m.NotChangeable})")}");
                if ((await reader.TapMethodIdsAsync(run.ObjectId!, ct)).Count > 0) leftovers.Add("a Temporary Access Pass is still registered");
            }
            var (assignments, unreadable) = await AssignmentsAsync(run.ObjectId!, ct);
            leftovers.AddRange(unreadable.Select(s => $"Azure role assignments in subscription {s} could not be read: check and remove them there"));
            leftovers.AddRange(assignments.Select(a => $"Azure role {BuiltInRoles.NameOf(a.RoleDefinitionId) ?? a.RoleDefinitionId} at {a.Scope} is still assigned"));
        }
        catch (DirectoryReadException e)
        {
            await TransientAsync(project, v, "verify", e.Category, ct);
            return;
        }
        // A request for the person opened after the plan (or anything else still open) would grant access again: a leftover.
        var planned = run.Steps.Where(s => s.Name == Close).Select(s => s.Target).ToHashSet(StringComparer.Ordinal);
        foreach (var r in (await stores.Requests.ListAsync(project.ProjectId, ct)).Select(x => x.Value)
                     .Where(r => r.Flow is { IsFinal: false } && string.Equals(r.DomainId, run.EnterpriseId, StringComparison.OrdinalIgnoreCase)))
            leftovers.Add(planned.Contains(r.Id)
                ? $"request {r.Id} is still open ({StatusText.Label(r.Flow!.Status)})"
                : $"request {r.Id} was opened after the plan ({StatusText.Label(r.Flow!.Status)}): reject it, or offboard again");
        // Steps that failed and nothing above shows (sessions, inventory, requests) are leftovers too, and so are sessions
        // of a directory-role holder (left to people).
        foreach (var s in run.Steps.Where(s => (s.Result == OffboardingStepResult.Failed && s.Name is Revoke or Leaver)
                                               || (s.Result == OffboardingStepResult.Skipped && s.Name == Revoke && s.Detail.EndsWith(ByPeople, StringComparison.Ordinal))))
            leftovers.Add($"{s.Name} {s.Target}: {s.Detail}");

        var now = clock.UtcNow;
        if (leftovers.Count > 0)
        {
            var e = new Escalation
            {
                Id = Escalation.MakeId(EscalationKind.OffboardingLeftover, $"{project.ProjectId}|{run.Id}"), ProjectId = project.ProjectId, Kind = EscalationKind.OffboardingLeftover,
                User = run.EnterpriseId, Reason = $"Offboarding {run.Id} left {leftovers.Count} thing(s): {string.Join("; ", leftovers)}. No completion mail was sent.",
                RequestedBy = Actor, CreatedAt = now, CorrelationId = run.CorrelationId, IsSample = project.IsSample,
            };
            try { await stores.Escalations.CreateAsync(e, ct); } catch (DocumentConflictException) { /* raised already */ }
            await issues.RecordAsync(project.ProjectId, null, $"offboarding:{run.Id}", IssueKind.NeedsDeveloper, IssueSeverity.High,
                $"Offboarding of {run.EnterpriseId} is partially completed: {leftovers.Count} leftover(s). Escalation {e.Id}; the PMO was not told it is complete.", null, ct);
            await SaveAsync(project, v, run with { Status = OffboardingStatus.PartiallyCompleted, Leftovers = leftovers, FinishedAt = now, UpdatedAt = now },
                "offboarding-partially-completed", $"{leftovers.Count} leftover(s), escalation {e.Id}", ct);
            return;
        }

        // Clean: one mail, to the project's PMO only (never the user, the manager or anyone else).
        var (to, _) = Notifier.Addresses(Recipients.Pmo(project));
        var name = run.DisplayName ?? run.EnterpriseId;
        var notified = false;
        if (to.Count > 0)
        {
            await notifier.EnqueueAsync(project.ProjectId, null, $"offboarding:{run.Id}", NotificationKind.OffboardingCompleted, to, $"Offboarding completed: {name}",
                $"Offboarding completed for {name} ({run.EnterpriseId}) at {Messages.Time(now)}. Reference {run.CorrelationId}.", ct);
            notified = true;
        }
        else
        {
            await issues.RecordAsync(project.ProjectId, null, $"offboarding-pmo:{run.Id}", IssueKind.NeedsDeveloper, IssueSeverity.High,
                $"Offboarding of {run.EnterpriseId} is complete, but the project has no PMO address: nobody was mailed. Fill in PMO on the Team contacts page.", null, ct);
        }
        await SaveAsync(project, v, run with { Status = OffboardingStatus.Completed, Leftovers = [], PmoNotified = notified, FinishedAt = now, UpdatedAt = now },
            "offboarding-completed", notified ? "verified clean; PMO mailed" : "verified clean; no PMO address", ct);
    }

    // ---- helpers -------------------------------------------------------------------------------------------------------

    /// <summary>A read failed (Graph or ARM unavailable): the run stays where it is and the next worker run tries again, at most 3 times.</summary>
    private async Task TransientAsync(Project project, Versioned<OffboardingRun> v, string phase, string category, CancellationToken ct)
    {
        var run = v.Value;
        var attempts = new Dictionary<string, int>(run.Attempts) { [phase] = run.Attempts.GetValueOrDefault(phase) + 1 };
        logger.LogWarning("offboarding {Phase} paused {Category} run={RunId}", phase, category, run.Id);
        if (attempts[phase] < MaxAttempts)
        {
            await stores.OffboardingRuns.ReplaceAsync(run with { Attempts = attempts, UpdatedAt = clock.UtcNow }, v.ETag, ct);
            return;
        }
        var now = clock.UtcNow;
        var leftovers = phase == "plan" ? [] : new List<string> { $"the {phase} could not read Entra or Azure ({category}): what was done is in the steps; check the rest by hand" };
        await SaveAsync(project, v, run with
        {
            Status = phase == "plan" ? OffboardingStatus.Failed : OffboardingStatus.PartiallyCompleted, Attempts = attempts, Leftovers = leftovers, FinishedAt = now, UpdatedAt = now,
        }, phase == "plan" ? "offboarding-failed" : "offboarding-partially-completed", $"{phase} failed {MaxAttempts} times ({category})", ct);
        if (phase != "plan")
            await issues.RecordAsync(project.ProjectId, null, $"offboarding:{run.Id}", IssueKind.DependencyFailure, IssueSeverity.High,
                $"Offboarding of {run.EnterpriseId} could not finish its {phase} ({category}).", null, ct);
    }

    private async Task SaveAsync(Project project, Versioned<OffboardingRun> v, OffboardingRun next, string action, string details, CancellationToken ct)
    {
        await stores.OffboardingRuns.ReplaceAsync(next, v.ETag, ct);
        await AuditAsync(project, action, next.EnterpriseId, $"run {next.Id}: {details}", next.CorrelationId, ct);
        logger.LogInformation("offboarding {Action} run={RunId} status={Status}", action, next.Id, next.Status);
    }

    private async Task AuditAsync(Project project, string action, string target, string details, string? cid, CancellationToken ct) =>
        await stores.AuditEvents.CreateAsync(new AuditEvent
        {
            Id = AuditEvent.NewId(clock.UtcNow), ProjectId = project.ProjectId, At = clock.UtcNow, Actor = Actor, Action = action, Target = target,
            CorrelationId = cid, Details = SecretMasker.Apply(details), IsSample = project.IsSample,
        }, ct);

    private static OffboardingStep Step(string name, string target, string action, OffboardingStepResult result, string detail) =>
        new() { Name = name, Target = target, Action = action, Result = result, Detail = detail };
}
