namespace CmsEvents.Unit.Tests.Application.Features;

using CmsEvents.Application.Common.Repositories;
using CmsEvents.Application.Features.ListEntities;
using CmsEvents.Domain.Entities;
using CmsEvents.Domain.Enums;
using FluentAssertions;
using Moq;
using Xunit;

/// <summary>
/// Covers <see cref="ListEntitiesHandler"/> — role-aware filter + DTO projection per ADR-007.
/// </summary>
public sealed class ListEntitiesHandlerTests
{
    private static readonly DateTime Now = new(2026, 8, 1, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Handle_AsNormalUser_RequestsFilteredList_AndOmitsAdminFields()
    {
        var visible = Entity.CreateFromPublish("a", version: 1, timestamp: Now, payload: "{}", now: Now);
        var queries = new Mock<IEntityQueries>();
        queries
            .Setup(q => q.ListAsync(false, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { visible });

        var sut = new ListEntitiesHandler(queries.Object);

        var response = await sut.Handle(new ListEntitiesQuery(UserRole.User, 100, Guid.NewGuid()), CancellationToken.None);

        response.Count.Should().Be(1);
        response.Entities.Single().Status.Should().BeNull("Status is omitted from the response for normal users");
        response.Entities.Single().IsDisabled.Should().BeNull("IsDisabled is omitted from the response for normal users");
        queries.Verify(q => q.ListAsync(false, It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_AsAdmin_RequestsUnfilteredList_AndPopulatesAdminFields()
    {
        var entity = Entity.CreateOrphanFromUnpublish("orphan", version: 1, timestamp: Now, payload: "{}", now: Now);
        var queries = new Mock<IEntityQueries>();
        queries
            .Setup(q => q.ListAsync(true, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { entity });

        var sut = new ListEntitiesHandler(queries.Object);

        var response = await sut.Handle(new ListEntitiesQuery(UserRole.Admin, 100, Guid.NewGuid()), CancellationToken.None);

        response.Entities.Single().Status.Should().Be(EntityStatus.Unpublished.ToString());
        response.Entities.Single().IsDisabled.Should().BeFalse();
        queries.Verify(q => q.ListAsync(true, It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_LimitFlowsThroughToQuery()
    {
        var queries = new Mock<IEntityQueries>();
        queries
            .Setup(q => q.ListAsync(It.IsAny<bool>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<Entity>());

        var sut = new ListEntitiesHandler(queries.Object);

        await sut.Handle(new ListEntitiesQuery(UserRole.User, Limit: 25, Guid.NewGuid()), CancellationToken.None);

        queries.Verify(q => q.ListAsync(It.IsAny<bool>(), 25, It.IsAny<CancellationToken>()), Times.Once);
    }
}
