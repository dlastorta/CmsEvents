namespace CmsEvents.Application.EventProcessing;

using System.Diagnostics;

/// <summary>
/// <see cref="ActivitySource"/> shared by all <see cref="IEventHandler"/> implementations to emit
/// custom spans per event type per ADR-014 ("Custom spans per event handler with eventId and version
/// as attributes"). Registered with OpenTelemetry in <c>Program.AddOpenTelemetry</c>.
///
/// Span naming: <c>ProcessEvent.{type}</c> (e.g., <c>ProcessEvent.publish</c>). Attributes set
/// per invocation: <c>event.id</c>, <c>event.version</c> (when applicable).
/// </summary>
public static class EventProcessingActivitySource
{
    public const string Name = "CmsEvents.EventProcessing";

    public static readonly ActivitySource Instance = new(Name);
}
