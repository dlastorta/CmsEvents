namespace CmsEvents.Unit.Tests.TestDoubles;

using CmsEvents.Domain.Abstractions;

/// <summary>
/// Deterministic <see cref="IClock"/> for unit tests. Time advances only when the test moves it.
/// </summary>
public sealed class FakeClock : IClock
{
    private DateTime _now;

    public FakeClock(DateTime? initial = null)
    {
        _now = initial ?? new DateTime(2026, 8, 1, 10, 0, 0, DateTimeKind.Utc);
    }

    public DateTime UtcNow => _now;

    public void Advance(TimeSpan by) => _now = _now.Add(by);

    public void Set(DateTime moment) => _now = moment;
}
