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
/// Covers <see cref="UnPublishEventHandler"/> per ADR-005 (idempotency) and ADR-006 spec corner case
/// (orphan unpublish creates the entity with Status=Unpublished).
/// </summary>
public sealed class UnPublishEventHandlerTests
{
    private static readonly DateTime Now = new(2026, 8, 1, 10, 0, 0, DateTimeKind.Utc);
    private const string NowIso = "2026-08-01T10:00:00Z";
    private static readonly JsonElement Payload = JsonDocument.Parse("{\"title\":\"corner-case\"}").RootElement;

    [Fact]
    public async Task UnPublish_ForOrphan_CreatesEntityWithUnpublishedStatus()
    {
        var repository = new Mock<IEntityRepository>();
        repository
            .Setup(r => r.FindByIdAsync("orphan-id", It.IsAny<CancellationToken>()))
            .ReturnsAsync((Entity?)null);

        var sut = new UnPublishEventHandler(repository.Object, new FakeClock(Now), NullLogger<UnPublishEventHandler>.Instance);

        var outcome = await sut.HandleAsync(NewEvent("orphan-id", version: 5), CancellationToken.None);

        outcome.Type.Should().Be(EventOutcomeType.Processed);
        repository.Verify(r => r.AddAsync(
            It.Is<Entity>(e =>
                e.Id == "orphan-id" &&
                e.Status == EntityStatus.Unpublished &&
                e.LastProcessedVersion == 5 &&
                !e.IsDisabled),
            It.IsAny<CancellationToken>()), Times.Once);
        repository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UnPublish_ForExistingLowerVersion_SkipsWithSupersededReason()
    {
        var existing = Entity.CreateFromPublish("id-1", version: 5, timestamp: Now, payload: "{}", now: Now);
        var repository = new Mock<IEntityRepository>();
        repository
            .Setup(r => r.FindByIdAsync("id-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(existing);

        var sut = new UnPublishEventHandler(repository.Object, new FakeClock(Now), NullLogger<UnPublishEventHandler>.Instance);

        var outcome = await sut.HandleAsync(NewEvent("id-1", version: 3), CancellationToken.None);

        outcome.Type.Should().Be(EventOutcomeType.Skipped);
        outcome.Reason.Should().Be("superseded_by_version");
        repository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UnPublish_ForExistingHigherVersion_AppliesAndFlipsStatus()
    {
        var existing = Entity.CreateFromPublish("id-1", version: 2, timestamp: Now, payload: "{}", now: Now);
        existing.Status.Should().Be(EntityStatus.Published);

        var repository = new Mock<IEntityRepository>();
        repository
            .Setup(r => r.FindByIdAsync("id-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(existing);

        var sut = new UnPublishEventHandler(repository.Object, new FakeClock(Now.AddMinutes(1)), NullLogger<UnPublishEventHandler>.Instance);

        var outcome = await sut.HandleAsync(NewEvent("id-1", version: 3), CancellationToken.None);

        outcome.Type.Should().Be(EventOutcomeType.Processed);
        existing.Status.Should().Be(EntityStatus.Unpublished);
        existing.LastProcessedVersion.Should().Be(3);
        repository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UnPublish_DoesNotTouchIsDisabled()
    {
        var existing = Entity.CreateFromPublish("id-1", version: 1, timestamp: Now, payload: "{}", now: Now);
        existing.Disable(Now);
        existing.IsDisabled.Should().BeTrue();

        var repository = new Mock<IEntityRepository>();
        repository
            .Setup(r => r.FindByIdAsync("id-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(existing);

        var sut = new UnPublishEventHandler(repository.Object, new FakeClock(Now), NullLogger<UnPublishEventHandler>.Instance);

        await sut.HandleAsync(NewEvent("id-1", version: 2), CancellationToken.None);

        existing.IsDisabled.Should().BeTrue("admin disable is sticky per ADR-007");
    }

    private static CmsEventEnvelope NewEvent(string id, int version) => new()
    {
        Type = CmsEventType.UnPublish,
        Id = id,
        Version = version,
        Timestamp = NowIso,
        Payload = Payload,
    };
}
