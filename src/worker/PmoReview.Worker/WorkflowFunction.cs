using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using PmoReview.Core.Workflow;

namespace PmoReview.Worker;

/// <summary>
/// Every 5 minutes (WORKFLOW_SCHEDULE): the AI agent's cycle. Finishes Entra cross-checks, verifies team reports,
/// reruns reviews after mapping-sheet changes, sends SLA reminders and escalations, delivers notifications, syncs
/// Azure DevOps work items and writes the daily summary. Read-only towards Entra and Azure RBAC.
/// </summary>
public sealed class WorkflowFunction(WorkflowWorker worker, ILogger<WorkflowFunction> logger)
{
    [Function("workflow-agent")]
    public async Task Run([TimerTrigger("%WORKFLOW_SCHEDULE%")] TimerInfo timer, CancellationToken ct)
    {
        var cid = Guid.NewGuid().ToString("n");
        var s = await worker.RunOnceAsync(cid, ct);
        logger.LogInformation("workflow-agent done advanced={Advanced} notifications={Notifications} workItems={WorkItems} daily={Daily} next={Next} cid={Cid}",
            s.Advanced, s.Notifications, s.WorkItems, s.DailySummary, timer.ScheduleStatus?.Next, cid);
    }
}
