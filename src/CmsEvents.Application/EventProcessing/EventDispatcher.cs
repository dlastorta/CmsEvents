namespace CmsEvents.Application.EventProcessing;

using CmsEvents.Contracts.Events;

/// <summary>
/// Routes a CMS event to the correct <see cref="IEventHandler"/> based on its type string.
/// Implements the Strategy pattern per ADR-004. Unknown event types raise
/// <see cref="UnknownEventTypeException"/> which the caller translates into a permanent failure
/// per ADR-008.
/// </summary>
public sealed class EventDispatcher
{
    private readonly IReadOnlyDictionary<string, IEventHandler> _handlersByType;

    public EventDispatcher(IEnumerable<IEventHandler> handlers)
    {
        ArgumentNullException.ThrowIfNull(handlers);

        _handlersByType = handlers.ToDictionary(
            handler => handler.EventType,
            handler => handler,
            StringComparer.Ordinal);
    }

    public Task<EventOutcome> DispatchAsync(CmsEventEnvelope evt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evt);

        if (!_handlersByType.TryGetValue(evt.Type, out var handler))
        {
            throw new UnknownEventTypeException(evt.Type);
        }

        return handler.HandleAsync(evt, cancellationToken);
    }
}
