using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using PmoReview.Core.Handoff;

namespace PmoReview.Worker;

/// <summary>Every 5 minutes (HANDOFF_SYNC_SCHEDULE): read Entra, update open hand-off records. Reads Entra only.</summary>
public sealed class HandoffSyncFunction(HandoffSync sync, ILogger<HandoffSyncFunction> logger)
{
    [Function("handoff-sync")]
    public async Task Run([TimerTrigger("%HANDOFF_SYNC_SCHEDULE%")] TimerInfo timer, CancellationToken ct)
    {
        var summary = await sync.RunOnceAsync(ct);
        logger.LogInformation("handoff-sync done checked={Checked} changed={Changed} errors={Errors} next={Next}",
            summary.Checked, summary.Changed, summary.Errors, timer.ScheduleStatus?.Next);
    }
}
