namespace CmsEvents.Unit.Tests.Application.EventProcessing;

using CmsEvents.Application.Common.Repositories;
using CmsEvents.Application.EventProcessing;
using CmsEvents.Application.EventProcessing.Handlers;
using CmsEvents.Contracts.Events;
using CmsEvents.Domain.Entities;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

/// <summary>
/// Covers <see cref="DeleteEventHandler"/> per ADR-005 (hard-delete) and ADR-006
/// (orphan-delete as skipped no-op).
/// </summary>
public sealed class DeleteEventHandlerTests
{
    private static readonly DateTime Now = new(2026, 8, 1, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Delete_ForOrphan_ReturnsSkipped_WithOrphanReason()
    {
        var repository = new Mock<IEntityRepository>();
        repository
            .Setup(r => r.FindByIdAsync("missing-id", It.IsAny<CancellationToken>()))
            .ReturnsAsync((Entity?)null);

        var sut = new DeleteEventHandler(repository.Object, NullLogger<DeleteEventHandler>.Instance);

        var outcome = await sut.HandleAsync(
            new CmsEventEnvelope { Type = CmsEventType.Delete, Id = "missing-id", Timestamp = Now },
            CancellationToken.None);

        outcome.Type.Should().Be(EventOutcomeType.Skipped);
        outcome.Reason.Should().Be("orphan_delete");
        repository.Verify(r => r.Remove(It.IsAny<Entity>()), Times.Never);
        repository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Delete_ForExisting_RemovesAndSaves()
    {
        var entity = Entity.CreateFromPublish("id-1", version: 1, timestamp: Now, payload: "{}", now: Now);
        var repository = new Mock<IEntityRepository>();
        repository
            .Setup(r => r.FindByIdAsync("id-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(entity);

        var sut = new DeleteEventHandler(repository.Object, NullLogger<DeleteEventHandler>.Instance);

        var outcome = await sut.HandleAsync(
            new CmsEventEnvelope { Type = CmsEventType.Delete, Id = "id-1", Timestamp = Now },
            CancellationToken.None);

        outcome.Type.Should().Be(EventOutcomeType.Processed);
        repository.Verify(r => r.Remove(entity), Times.Once);
        repository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }
}
