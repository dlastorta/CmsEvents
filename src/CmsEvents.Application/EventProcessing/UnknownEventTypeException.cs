namespace CmsEvents.Application.EventProcessing;

/// <summary>
/// Thrown by <see cref="EventDispatcher"/> when no <see cref="IEventHandler"/> is registered for the
/// incoming event type. Translated by the caller into a permanent failure with reason
/// <c>unknown_event_type</c> per ADR-008.
/// </summary>
public sealed class UnknownEventTypeException : Exception
{
    public UnknownEventTypeException(string eventType)
        : base($"No handler registered for event type '{eventType}'.")
    {
        EventType = eventType;
    }

    public string EventType { get; }
}
