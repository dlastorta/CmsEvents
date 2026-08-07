namespace CmsEvents.Application.EventProcessing;

using CmsEvents.Contracts.Events;

/// <summary>
/// Handles a single CMS event of a specific <see cref="EventType"/> per the Strategy pattern (ADR-004).
/// Registered as Scoped in DI. Resolved by <see cref="EventDispatcher"/> at runtime via type string.
///
/// Handlers are transport-agnostic (per ADR-002 rule 3) and can be invoked from any entry point:
/// the HTTP endpoint today, a BackgroundService or Kafka consumer tomorrow (per ADR-009).
/// </summary>
public interface IEventHandler
{
    /// <summary>
    /// Wire-format event type string this handler processes. Must match one of the values in
    /// <see cref="CmsEventType"/>.
    /// </summary>
    string EventType { get; }

    /// <summary>
    /// Process the event and return its outcome. Must not throw for expected outcomes
    /// (idempotency skip, orphan delete, validation failure) — return <see cref="EventOutcome"/>.
    /// May throw for unexpected infrastructure failures (transient DB errors), which the caller
    /// will retry per ADR-008 Polly policy.
    /// </summary>
    Task<EventOutcome> HandleAsync(CmsEventEnvelope evt, CancellationToken cancellationToken);
}
