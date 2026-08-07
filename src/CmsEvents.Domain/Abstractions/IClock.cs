namespace CmsEvents.Domain.Abstractions;

/// <summary>
/// Abstraction over the system clock. Enables deterministic tests and
/// preserves the Domain-inward-only dependency rule (per ADR-001 / ADR-002).
/// Implementation lives in Infrastructure.
/// </summary>
public interface IClock
{
    /// <summary>
    /// The current point in time, in UTC.
    /// </summary>
    DateTime UtcNow { get; }
}
