namespace CmsEvents.Application.EventProcessing.Handlers;

using System.Text.Json;
using CmsEvents.Application.Common.Repositories;
using CmsEvents.Contracts.Events;
using CmsEvents.Domain.Abstractions;
using CmsEvents.Domain.Entities;
using CmsEvents.Domain.ValueObjects;
using Microsoft.Extensions.Logging;

/// <summary>
/// Handles <c>publish</c> events per ADR-005 (idempotency rule) and ADR-006 (unknown-entity handling).
/// Publish for a new id creates the entity; for an existing id, applies idempotency comparison
/// (higher version → apply; lower → skip; equal → tie-break on timestamp).
/// </summary>
public sealed class PublishEventHandler : IEventHandler
{
    private readonly IEntityRepository _repository;
    private readonly IClock _clock;
    private readonly ILogger<PublishEventHandler> _logger;

    public PublishEventHandler(
        IEntityRepository repository,
        IClock clock,
        ILogger<PublishEventHandler> logger)
    {
        _repository = repository;
        _clock = clock;
        _logger = logger;
    }

    public string EventType => CmsEventType.Publish;

    public async Task<EventOutcome> HandleAsync(CmsEventEnvelope evt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evt);
        using var activity = EventProcessingActivitySource.Instance.StartActivity($"ProcessEvent.{EventType}");
        activity?.SetTag("event.id", evt.Id);
        activity?.SetTag("event.version", evt.Version);

        var version = evt.Version ?? throw new InvalidOperationException("Publish event must have a version (validation should catch this).");
        var payload = evt.Payload?.GetRawText() ?? throw new InvalidOperationException("Publish event must have a payload (validation should catch this).");

        var timestamp = evt.GetTimestampUtc();
        var existing = await _repository.FindByIdAsync(evt.Id, cancellationToken);
        var now = _clock.UtcNow;

        if (existing is null)
        {
            var created = Entity.CreateFromPublish(evt.Id, version, timestamp, payload, now);
            await _repository.AddAsync(created, cancellationToken);
            await _repository.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Publish applied (new): id={Id}, version={Version}", evt.Id, version);
            return EventOutcome.Processed();
        }

        var decision = existing.EvaluateForApply(version, timestamp);
        if (decision.Outcome == IdempotencyOutcome.Skip)
        {
            _logger.LogWarning(
                "Publish skipped: id={Id}, incomingVersion={Incoming}, storedVersion={Stored}, reason={Reason}",
                evt.Id, version, existing.LastProcessedVersion, decision.SkipReason);
            return EventOutcome.Skipped(decision.SkipReason!);
        }

        existing.ApplyPublish(version, timestamp, payload, now);
        await _repository.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Publish applied (update): id={Id}, version={Version}", evt.Id, version);
        return EventOutcome.Processed();
    }
}
