namespace CmsEvents.Application.EventProcessing.Handlers;

using CmsEvents.Application.Common.Repositories;
using CmsEvents.Contracts.Events;
using CmsEvents.Domain.Abstractions;
using CmsEvents.Domain.Entities;
using CmsEvents.Domain.ValueObjects;
using Microsoft.Extensions.Logging;

/// <summary>
/// Handles <c>unPublish</c> events per ADR-005 (idempotency) and ADR-006 (unknown-entity handling).
/// Unpublish for a new id is the corner case explicitly called out by the spec — upsert with
/// <c>Status=Unpublished</c> to preserve the CMS's authoritative view under out-of-order delivery.
/// </summary>
public sealed class UnPublishEventHandler : IEventHandler
{
    private readonly IEntityRepository _repository;
    private readonly IClock _clock;
    private readonly ILogger<UnPublishEventHandler> _logger;

    public UnPublishEventHandler(
        IEntityRepository repository,
        IClock clock,
        ILogger<UnPublishEventHandler> logger)
    {
        _repository = repository;
        _clock = clock;
        _logger = logger;
    }

    public string EventType => CmsEventType.UnPublish;

    public async Task<EventOutcome> HandleAsync(CmsEventEnvelope evt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evt);
        using var activity = EventProcessingActivitySource.Instance.StartActivity($"ProcessEvent.{EventType}");
        activity?.SetTag("event.id", evt.Id);
        activity?.SetTag("event.version", evt.Version);

        var version = evt.Version ?? throw new InvalidOperationException("UnPublish event must have a version (validation should catch this).");
        var payload = evt.Payload?.GetRawText() ?? throw new InvalidOperationException("UnPublish event must have a payload (validation should catch this).");

        var timestamp = evt.GetTimestampUtc();
        var existing = await _repository.FindByIdAsync(evt.Id, cancellationToken);
        var now = _clock.UtcNow;

        if (existing is null)
        {
            // Orphan unpublish — spec corner case (ADR-006). Upsert with Status=Unpublished.
            var created = Entity.CreateOrphanFromUnpublish(evt.Id, version, timestamp, payload, now);
            await _repository.AddAsync(created, cancellationToken);
            await _repository.SaveChangesAsync(cancellationToken);
            _logger.LogInformation(
                "UnPublish applied (orphan upsert): id={Id}, version={Version}", evt.Id, version);
            return EventOutcome.Processed();
        }

        var decision = existing.EvaluateForApply(version, timestamp);
        if (decision.Outcome == IdempotencyOutcome.Skip)
        {
            _logger.LogWarning(
                "UnPublish skipped: id={Id}, incomingVersion={Incoming}, storedVersion={Stored}, reason={Reason}",
                evt.Id, version, existing.LastProcessedVersion, decision.SkipReason);
            return EventOutcome.Skipped(decision.SkipReason!);
        }

        existing.ApplyUnpublish(version, timestamp, payload, now);
        await _repository.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("UnPublish applied: id={Id}, version={Version}", evt.Id, version);
        return EventOutcome.Processed();
    }
}
