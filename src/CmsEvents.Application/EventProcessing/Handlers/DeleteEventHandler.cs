namespace CmsEvents.Application.EventProcessing.Handlers;

using CmsEvents.Application.Common.Repositories;
using CmsEvents.Contracts.Events;
using Microsoft.Extensions.Logging;

/// <summary>
/// Handles <c>delete</c> events per ADR-005 (hard-delete) and ADR-006 (orphan no-op).
/// Hard-delete for a known id removes the row entirely (no tombstone per spec item 2).
/// Orphan delete (id not found locally) is a no-op counted as skipped with reason
/// <c>orphan_delete</c>, logged at Warning level for anomaly detection.
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

        _repository.Remove(existing);
        await _repository.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Delete applied (hard-delete): id={Id}", evt.Id);
        return EventOutcome.Processed();
    }
}
