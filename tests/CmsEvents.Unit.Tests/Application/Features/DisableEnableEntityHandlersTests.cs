namespace CmsEvents.Unit.Tests.Application.Features;

using CmsEvents.Application.Common.Repositories;
using CmsEvents.Application.Features.DisableEntity;
using CmsEvents.Application.Features.EnableEntity;
using CmsEvents.Domain.Entities;
using CmsEvents.Unit.Tests.TestDoubles;
using FluentAssertions;
using Moq;
using Xunit;

/// <summary>
/// Covers <see cref="DisableEntityHandler"/> and <see cref="EnableEntityHandler"/> per ADR-007 —
/// idempotency, not-found path (null → API 404), sticky admin flag preserved by domain method.
/// </summary>
public sealed class DisableEnableEntityHandlersTests
{
    private static readonly DateTime Now = new(2026, 8, 1, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Disable_ForExistingEntity_SetsIsDisabledAndSaves()
    {
        var entity = Entity.CreateFromPublish("id-1", version: 1, timestamp: Now, payload: "{}", now: Now);
        entity.IsDisabled.Should().BeFalse();

        var repository = new Mock<IEntityRepository>();
        repository
            .Setup(r => r.FindByIdAsync("id-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(entity);

        var sut = new DisableEntityHandler(repository.Object, new FakeClock(Now));

        var response = await sut.Handle(new DisableEntityCommand("id-1", Guid.NewGuid()), CancellationToken.None);

        response.Should().NotBeNull();
        response!.IsDisabled.Should().BeTrue();
        entity.IsDisabled.Should().BeTrue();
        repository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Disable_AlreadyDisabled_IsIdempotent_StillReturnsOk()
    {
        var entity = Entity.CreateFromPublish("id-1", version: 1, timestamp: Now, payload: "{}", now: Now);
        entity.Disable(Now);
        entity.IsDisabled.Should().BeTrue();

        var repository = new Mock<IEntityRepository>();
        repository
            .Setup(r => r.FindByIdAsync("id-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(entity);

        var sut = new DisableEntityHandler(repository.Object, new FakeClock(Now));

        var response = await sut.Handle(new DisableEntityCommand("id-1", Guid.NewGuid()), CancellationToken.None);

        response!.IsDisabled.Should().BeTrue();
    }

    [Fact]
    public async Task Disable_NotFound_ReturnsNull_ForApiToMapTo404()
    {
        var repository = new Mock<IEntityRepository>();
        repository
            .Setup(r => r.FindByIdAsync("missing", It.IsAny<CancellationToken>()))
            .ReturnsAsync((Entity?)null);

        var sut = new DisableEntityHandler(repository.Object, new FakeClock(Now));

        var response = await sut.Handle(new DisableEntityCommand("missing", Guid.NewGuid()), CancellationToken.None);

        response.Should().BeNull();
        repository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Enable_ForDisabledEntity_ClearsIsDisabledAndSaves()
    {
        var entity = Entity.CreateFromPublish("id-1", version: 1, timestamp: Now, payload: "{}", now: Now);
        entity.Disable(Now);

        var repository = new Mock<IEntityRepository>();
        repository
            .Setup(r => r.FindByIdAsync("id-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(entity);

        var sut = new EnableEntityHandler(repository.Object, new FakeClock(Now));

        var response = await sut.Handle(new EnableEntityCommand("id-1", Guid.NewGuid()), CancellationToken.None);

        response!.IsDisabled.Should().BeFalse();
        entity.IsDisabled.Should().BeFalse();
        repository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Enable_NotFound_ReturnsNull()
    {
        var repository = new Mock<IEntityRepository>();
        repository
            .Setup(r => r.FindByIdAsync("missing", It.IsAny<CancellationToken>()))
            .ReturnsAsync((Entity?)null);

        var sut = new EnableEntityHandler(repository.Object, new FakeClock(Now));

        var response = await sut.Handle(new EnableEntityCommand("missing", Guid.NewGuid()), CancellationToken.None);

        response.Should().BeNull();
    }
}
