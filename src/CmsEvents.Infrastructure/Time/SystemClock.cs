namespace CmsEvents.Infrastructure.Time;

using CmsEvents.Domain.Abstractions;

/// <summary>
/// Default <see cref="IClock"/> implementation using <see cref="DateTime.UtcNow"/>.
/// Registered as Singleton in DI.
/// </summary>
public sealed class SystemClock : IClock
{
    public DateTime UtcNow => DateTime.UtcNow;
}
