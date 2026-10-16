using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using PmoReview.Core.Handoff;

namespace PmoReview.Worker;

/// <summary>Every 5 minutes (HANDOFF_SYNC_SCHEDULE): read Entra, update open hand-off records, and in auto-assign mode file the Entra assignment for Queued ones.</summary>
public sealed class HandoffSyncFunction(HandoffSync sync, IServiceProvider services, ILogger<HandoffSyncFunction> logger)
{
    [Function("handoff-sync")]
    public async Task Run([TimerTrigger("%HANDOFF_SYNC_SCHEDULE%")] TimerInfo timer, CancellationToken ct)
    {
        // Packages first, so a mapping published a moment ago gets its Entra link as soon as possible.
        if (services.GetService(typeof(PackageSync)) is PackageSync packages)
        {
            var published = await packages.RunOnceAsync(ct);
            if (published > 0) logger.LogInformation("package-sync published={Published}", published);
        }
        var summary = await sync.RunOnceAsync(ct);
        logger.LogInformation("handoff-sync done checked={Checked} changed={Changed} errors={Errors} next={Next}",
            summary.Checked, summary.Changed, summary.Errors, timer.ScheduleStatus?.Next);
    }
}
