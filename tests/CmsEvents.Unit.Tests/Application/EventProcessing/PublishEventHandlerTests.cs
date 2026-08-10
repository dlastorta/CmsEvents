namespace CmsEvents.Unit.Tests.Application.EventProcessing;

using System.Text.Json;
using CmsEvents.Application.Common.Repositories;
using CmsEvents.Application.EventProcessing;
using CmsEvents.Application.EventProcessing.Handlers;
using CmsEvents.Contracts.Events;
using CmsEvents.Domain.Entities;
using CmsEvents.Domain.Enums;
using CmsEvents.Unit.Tests.TestDoubles;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

/// <summary>
/// Covers <see cref="PublishEventHandler"/> per ADR-005 (idempotency) and ADR-006 (new entity creation).
/// </summary>
public sealed class PublishEventHandlerTests
{
    private static readonly DateTime Now = new(2026, 8, 1, 10, 0, 0, DateTimeKind.Utc);
    private const string NowIso = "2026-08-01T10:00:00Z";
    private static readonly JsonElement Payload = JsonDocument.Parse("{\"title\":\"hello\"}").RootElement;

    [Fact]
    public async Task Publish_ForNewEntity_CreatesAndSaves()
    {
        // Assertions capture the FULL entity state passed to AddAsync — the mock-call verify is
        // secondary evidence of persistence intent, not the primary success criterion.
        Entity? captured = null;
        var repository = new Mock<IEntityRepository>();
        repository
            .Setup(r => r.FindByIdAsync("new-id", It.IsAny<CancellationToken>()))
            .ReturnsAsync((Entity?)null);
        repository
            .Setup(r => r.AddAsync(It.IsAny<Entity>(), It.IsAny<CancellationToken>()))
            .Callback<Entity, CancellationToken>((e, _) => captured = e)
            .Returns(Task.CompletedTask);

        var clock = new FakeClock(Now);
        var sut = new PublishEventHandler(repository.Object, clock, NullLogger<PublishEventHandler>.Instance);

        var outcome = await sut.HandleAsync(
            NewEvent("new-id", version: 1),
            CancellationToken.None);

        outcome.Type.Should().Be(EventOutcomeType.Processed);

        captured.Should().NotBeNull("AddAsync must have been called with a new Entity");
        captured!.Id.Should().Be("new-id");
        captured.Status.Should().Be(EntityStatus.Published);
        captured.IsDisabled.Should().BeFalse();
        captured.LastProcessedVersion.Should().Be(1);
        captured.LastProcessedTimestamp.Should().Be(Now);
        captured.Payload.Should().Contain("\"title\"").And.Contain("\"hello\"");
        captured.CreatedAt.Should().Be(Now);
        captured.UpdatedAt.Should().Be(Now);

        repository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Publish_ForSupersededVersion_ReturnsSkipped()
    {
        var existing = Entity.CreateFromPublish("id-1", version: 5, timestamp: Now, payload: "{}", now: Now);
        var repository = new Mock<IEntityRepository>();
        repository
            .Setup(r => r.FindByIdAsync("id-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(existing);

        var clock = new FakeClock(Now);
        var sut = new PublishEventHandler(repository.Object, clock, NullLogger<PublishEventHandler>.Instance);

        var outcome = await sut.HandleAsync(
            NewEvent("id-1", version: 3),
            CancellationToken.None);

        outcome.Type.Should().Be(EventOutcomeType.Skipped);
        outcome.Reason.Should().Be("superseded_by_version");
        repository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Publish_ForHigherVersion_AppliesAndSaves()
    {
        var existing = Entity.CreateFromPublish("id-1", version: 2, timestamp: Now, payload: "{\"v\":\"old\"}", now: Now);
        var repository = new Mock<IEntityRepository>();
        repository
            .Setup(r => r.FindByIdAsync("id-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(existing);

        var laterClock = Now.AddMinutes(1);
        var sut = new PublishEventHandler(repository.Object, new FakeClock(laterClock), NullLogger<PublishEventHandler>.Instance);

        var outcome = await sut.HandleAsync(
            NewEvent("id-1", version: 3),
            CancellationToken.None);

        outcome.Type.Should().Be(EventOutcomeType.Processed);

        // Full state assertion on the mutated aggregate — every ADR-005 field is checked, not
        // just "SaveChanges was called".
        existing.Status.Should().Be(EntityStatus.Published);
        existing.LastProcessedVersion.Should().Be(3);
        existing.LastProcessedTimestamp.Should().Be(Now, "event timestamp is used, not clock time");
        existing.UpdatedAt.Should().Be(laterClock, "clock drives UpdatedAt, not the event timestamp");
        existing.Payload.Should().Contain("\"title\"").And.Contain("\"hello\"", "payload must be replaced with the new event's body");
        existing.Payload.Should().NotContain("\"old\"", "the previous payload should not linger");

        repository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    private static CmsEventEnvelope NewEvent(string id, int version) => new()
    {
        Type = CmsEventType.Publish,
        Id = id,
        Version = version,
        Timestamp = NowIso,
        Payload = Payload,
    };
}
