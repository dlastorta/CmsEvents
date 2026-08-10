namespace CmsEvents.Application.EventProcessing.Handlers;

using CmsEvents.Application.Common.Repositories;
using CmsEvents.Contracts.Events;
using CmsEvents.Domain.ValueObjects;
using Microsoft.Extensions.Logging;

/// <summary>
/// Handles <c>delete</c> events per ADR-005 (hard-delete + timestamp ordering) and
/// ADR-006 (orphan no-op). Hard-delete for a known id removes the row entirely (no
/// tombstone per spec item 2). Two skip cases:
/// <list type="bullet">
///   <item><description><c>orphan_delete</c>: id not found locally (never observed, or already deleted).</description></item>
///   <item><description><c>stale_delete</c>: id exists but a newer publish/unpublish has already advanced its state — applying the delete would erase valid state. Guards against late/reordered/replayed delete events per ADR-005.</description></item>
/// </list>
/// Both skips are logged at Warning level for anomaly detection.
/// </summary>
public sealed class DeleteEventHandler : IEventHandler
{
    private const string OrphanDeleteReason = "orphan_delete";

    private readonly IEntityRepository _repository;
    private readonly ILogger<DeleteEventHandler> _logger;

    public DeleteEventHandler(IEntityRepository repository, ILogger<DeleteEventHandler> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    public string EventType => CmsEventType.Delete;

    public async Task<EventOutcome> HandleAsync(CmsEventEnvelope evt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evt);
        using var activity = EventProcessingActivitySource.Instance.StartActivity($"ProcessEvent.{EventType}");
        activity?.SetTag("event.id", evt.Id);

        var existing = await _repository.FindByIdAsync(evt.Id, cancellationToken);

        if (existing is null)
        {
            _logger.LogWarning(
                "Delete skipped as orphan no-op: id={Id}, timestamp={Timestamp}",
                evt.Id, evt.Timestamp ?? "(none)");
            return EventOutcome.Skipped(OrphanDeleteReason);
        }

        var incomingTimestamp = evt.GetTimestampUtc();
        var decision = existing.EvaluateForDelete(incomingTimestamp);
        if (decision.Outcome == IdempotencyOutcome.Skip)
        {
            _logger.LogWarning(
                "Delete skipped as stale — entity has newer state. id={Id}, deleteTimestamp={DeleteTimestamp}, " +
                "storedTimestamp={StoredTimestamp}, storedVersion={StoredVersion}",
                evt.Id, incomingTimestamp, existing.LastProcessedTimestamp, existing.LastProcessedVersion);
            return EventOutcome.Skipped(decision.SkipReason!);
        }

        _repository.Remove(existing);
        await _repository.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Delete applied (hard-delete): id={Id}", evt.Id);
        return EventOutcome.Processed();
    }
}
