using PmoReview.Core.Common;
using PmoReview.Core.Processing;

namespace PmoReview.Adapters.Dispatch;

/// <summary>Minimal queue send abstraction. The paid environment binds it to Storage Queues or Service Bus.</summary>
public interface IQueueClient
{
    Task SendAsync(string message, CancellationToken ct);
}

/// <summary>
/// DISABLED in the pilot (needs a Storage account or Service Bus, both paid/forbidden here).
/// Target design: Analyze enqueues {projectId, reviewRunId, correlationId}; a queue-triggered
/// Function calls <see cref="QueueMessageHandler"/>, which runs the same <see cref="ReviewProcessor"/>.
/// The Analyze call then returns 202 immediately and the SPA polls. See docs/PROMOTION.md.
/// </summary>
public sealed class QueueDispatcher(IQueueClient queue) : IReviewDispatcher
{
    public string Name => "Queue";

    public Task DispatchAsync(string projectId, string reviewRunId, string correlationId) =>
        queue.SendAsync(AppJson.Serialize(new DispatchMessage { ProjectId = projectId, ReviewRunId = reviewRunId, CorrelationId = correlationId }), CancellationToken.None);
}

/// <summary>Body of the future queue-triggered Function. Poison messages are dropped, never guessed at.</summary>
public sealed class QueueMessageHandler(ReviewProcessor processor)
{
    public async Task<bool> HandleAsync(string message)
    {
        var m = DispatchMessage.Parse(message);
        if (m is null || string.IsNullOrWhiteSpace(m.ProjectId) || string.IsNullOrWhiteSpace(m.ReviewRunId)) return false;
        await processor.ProcessAsync(m.ProjectId, m.ReviewRunId, m.CorrelationId);
        return true;
    }
}

public static class DispatcherFactory
{
    public static IReviewDispatcher Create(PilotSettings settings, ReviewProcessor processor, IQueueClient? queue = null)
    {
        switch (settings.Dispatcher)
        {
            case "Inline":
                return new InlineDispatcher(processor);
            case "Queue":
                if (!settings.AllowPaidServices)
                    throw new InvalidOperationException("REVIEW_DISPATCHER=Queue requires ALLOW_PAID_SERVICES=true. The zero-cost pilot uses Inline.");
                return new QueueDispatcher(queue ?? throw new InvalidOperationException("REVIEW_DISPATCHER=Queue needs a queue client; none is configured in the pilot."));
            default:
                throw new InvalidOperationException($"Unknown REVIEW_DISPATCHER '{settings.Dispatcher}'.");
        }
    }
}
