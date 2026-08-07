namespace CmsEvents.Unit.Tests.Application.Features;

using CmsEvents.Application.Common.Repositories;
using CmsEvents.Application.Features.GetEntity;
using CmsEvents.Domain.Entities;
using CmsEvents.Domain.Enums;
using FluentAssertions;
using Moq;
using Xunit;

/// <summary>
/// Covers <see cref="GetEntityHandler"/> — role-aware find + null-on-miss → API 404 mapping.
/// </summary>
public sealed class GetEntityHandlerTests
{
    private static readonly DateTime Now = new(2026, 8, 1, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Handle_AsNormalUser_ForVisibleEntity_ReturnsProjectedDto()
    {
        var entity = Entity.CreateFromPublish("visible", version: 1, timestamp: Now, payload: "{}", now: Now);
        var queries = new Mock<IEntityQueries>();
        queries
            .Setup(q => q.FindByIdAsync("visible", false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(entity);

        var sut = new GetEntityHandler(queries.Object);

        var result = await sut.Handle(new GetEntityQuery("visible", UserRole.User, Guid.NewGuid()), CancellationToken.None);

        result.Should().NotBeNull();
        result!.Id.Should().Be("visible");
        result.Status.Should().BeNull("normal user response omits Status per ADR-007");
        result.IsDisabled.Should().BeNull();
    }

    [Fact]
    public async Task Handle_AsAdmin_ForAnyEntity_ReturnsWithAdminFields()
    {
        var entity = Entity.CreateOrphanFromUnpublish("hidden", version: 1, timestamp: Now, payload: "{}", now: Now);
        var queries = new Mock<IEntityQueries>();
        queries
            .Setup(q => q.FindByIdAsync("hidden", true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(entity);

        var sut = new GetEntityHandler(queries.Object);

        var result = await sut.Handle(new GetEntityQuery("hidden", UserRole.Admin, Guid.NewGuid()), CancellationToken.None);

        result.Should().NotBeNull();
        result!.Status.Should().Be(EntityStatus.Unpublished.ToString());
        result.IsDisabled.Should().BeFalse();
    }

    [Fact]
    public async Task Handle_WhenQueryReturnsNull_ReturnsNull_ForApiToMapTo404()
    {
        var queries = new Mock<IEntityQueries>();
        queries
            .Setup(q => q.FindByIdAsync("missing", It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Entity?)null);

        var sut = new GetEntityHandler(queries.Object);

        var result = await sut.Handle(new GetEntityQuery("missing", UserRole.Admin, Guid.NewGuid()), CancellationToken.None);

        result.Should().BeNull();
    }
}
