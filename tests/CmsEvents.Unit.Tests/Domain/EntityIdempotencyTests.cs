namespace CmsEvents.Unit.Tests.Domain;

using CmsEvents.Domain.Entities;
using CmsEvents.Domain.ValueObjects;
using FluentAssertions;
using Xunit;

/// <summary>
/// Covers the version + timestamp idempotency rule per ADR-005.
/// Exercised at the Domain level — no persistence involved.
/// </summary>
public sealed class EntityIdempotencyTests
{
    private static readonly DateTime Now = new(2026, 8, 1, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void EvaluateForApply_HigherVersion_ReturnsApply()
    {
        var entity = Entity.CreateFromPublish("id-1", version: 3, timestamp: Now, payload: "{}", now: Now);

        var decision = entity.EvaluateForApply(incomingVersion: 4, incomingTimestamp: Now.AddMinutes(1));

        decision.Outcome.Should().Be(IdempotencyOutcome.Apply);
    }

    [Fact]
    public void EvaluateForApply_LowerVersion_ReturnsSkipSuperseded()
    {
        var entity = Entity.CreateFromPublish("id-1", version: 5, timestamp: Now, payload: "{}", now: Now);

        var decision = entity.EvaluateForApply(incomingVersion: 3, incomingTimestamp: Now.AddMinutes(1));

        decision.Outcome.Should().Be(IdempotencyOutcome.Skip);
        decision.SkipReason.Should().Be("superseded_by_version");
    }

    [Fact]
    public void EvaluateForApply_EqualVersion_LaterTimestamp_ReturnsApply()
    {
        var entity = Entity.CreateFromPublish("id-1", version: 3, timestamp: Now, payload: "{}", now: Now);

        var decision = entity.EvaluateForApply(incomingVersion: 3, incomingTimestamp: Now.AddSeconds(1));

        decision.Outcome.Should().Be(IdempotencyOutcome.Apply);
    }

    [Fact]
    public void EvaluateForApply_EqualVersion_EarlierOrEqualTimestamp_ReturnsSkipDuplicate()
    {
        var entity = Entity.CreateFromPublish("id-1", version: 3, timestamp: Now, payload: "{}", now: Now);

        var decision = entity.EvaluateForApply(incomingVersion: 3, incomingTimestamp: Now.AddSeconds(-1));

        decision.Outcome.Should().Be(IdempotencyOutcome.Skip);
        decision.SkipReason.Should().Be("duplicate");
    }

    [Fact]
    public void EvaluateForDelete_NewerTimestamp_ReturnsApply()
    {
        var entity = Entity.CreateFromPublish("id-1", version: 3, timestamp: Now, payload: "{}", now: Now);

        var decision = entity.EvaluateForDelete(incomingTimestamp: Now.AddSeconds(1));

        decision.Outcome.Should().Be(IdempotencyOutcome.Apply);
    }

    [Fact]
    public void EvaluateForDelete_OlderTimestamp_ReturnsSkipStaleDelete()
    {
        // Scenario: delete for id-1 was network-reordered and arrives after a newer publish.
        // Applying it would erase the newer state.
        var entity = Entity.CreateFromPublish("id-1", version: 5, timestamp: Now, payload: "{}", now: Now);

        var decision = entity.EvaluateForDelete(incomingTimestamp: Now.AddSeconds(-10));

        decision.Outcome.Should().Be(IdempotencyOutcome.Skip);
        decision.SkipReason.Should().Be("stale_delete");
    }

    [Fact]
    public void EvaluateForDelete_EqualTimestamp_ReturnsSkipStaleDelete()
    {
        // Equal-timestamp is fail-safe skipped — a delete "at the same instant" as the last
        // observed state has no defensible interpretation, so we protect against replays.
        var entity = Entity.CreateFromPublish("id-1", version: 3, timestamp: Now, payload: "{}", now: Now);

        var decision = entity.EvaluateForDelete(incomingTimestamp: Now);

        decision.Outcome.Should().Be(IdempotencyOutcome.Skip);
        decision.SkipReason.Should().Be("stale_delete");
    }

    [Fact]
    public void CreateOrphanFromUnpublish_YieldsUnpublishedStatus()
    {
        var entity = Entity.CreateOrphanFromUnpublish("id-1", version: 2, timestamp: Now, payload: "{}", now: Now);

        entity.Status.ToString().Should().Be("Unpublished");
        entity.IsDisabled.Should().BeFalse();
        entity.LastProcessedVersion.Should().Be(2);
    }

    [Fact]
    public void Disable_Then_Enable_IsIdempotent()
    {
        var entity = Entity.CreateFromPublish("id-1", version: 1, timestamp: Now, payload: "{}", now: Now);

        entity.Disable(Now);
        entity.Disable(Now); // idempotent
        entity.IsDisabled.Should().BeTrue();

        entity.Enable(Now);
        entity.Enable(Now); // idempotent
        entity.IsDisabled.Should().BeFalse();
    }

    [Fact]
    public void ApplyPublish_DoesNotTouch_IsDisabled()
    {
        var entity = Entity.CreateFromPublish("id-1", version: 1, timestamp: Now, payload: "{}", now: Now);
        entity.Disable(Now);

        entity.ApplyPublish(version: 2, timestamp: Now.AddMinutes(1), payload: "{\"a\":1}", now: Now.AddMinutes(1));

        entity.IsDisabled.Should().BeTrue("admin disable is sticky per ADR-007");
        entity.LastProcessedVersion.Should().Be(2);
    }
}
