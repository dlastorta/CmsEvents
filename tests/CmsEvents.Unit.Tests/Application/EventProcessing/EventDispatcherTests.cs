namespace CmsEvents.Unit.Tests.Application.EventProcessing;

using CmsEvents.Application.EventProcessing;
using CmsEvents.Contracts.Events;
using FluentAssertions;
using Moq;
using Xunit;

/// <summary>
/// Covers <see cref="EventDispatcher"/> per ADR-004 — dictionary lookup by event type,
/// unknown types throw for Application to translate into a permanent failure per ADR-008.
/// </summary>
public sealed class EventDispatcherTests
{
    [Fact]
    public async Task Dispatch_KnownType_RoutesToMatchingHandler()
    {
        var publishHandler = MockHandler(CmsEventType.Publish);
        var unpublishHandler = MockHandler(CmsEventType.UnPublish);
        var deleteHandler = MockHandler(CmsEventType.Delete);

        var sut = new EventDispatcher(new[]
        {
            publishHandler.Object,
            unpublishHandler.Object,
            deleteHandler.Object,
        });

        var evt = NewEvent(CmsEventType.Publish, "id-1");
        await sut.DispatchAsync(evt, CancellationToken.None);

        publishHandler.Verify(h => h.HandleAsync(evt, It.IsAny<CancellationToken>()), Times.Once);
        unpublishHandler.Verify(h => h.HandleAsync(It.IsAny<CmsEventEnvelope>(), It.IsAny<CancellationToken>()), Times.Never);
        deleteHandler.Verify(h => h.HandleAsync(It.IsAny<CmsEventEnvelope>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Dispatch_UnknownType_ThrowsUnknownEventTypeException()
    {
        var sut = new EventDispatcher(new[]
        {
            MockHandler(CmsEventType.Publish).Object,
        });

        var evt = NewEvent("archive", "id-1");

        var act = async () => await sut.DispatchAsync(evt, CancellationToken.None);

        var thrown = await act.Should().ThrowAsync<UnknownEventTypeException>();
        thrown.Which.EventType.Should().Be("archive");
    }

    [Fact]
    public void Dispatch_IsCaseSensitive()
    {
        var sut = new EventDispatcher(new[]
        {
            MockHandler(CmsEventType.Publish).Object,
        });

        var evt = NewEvent("PUBLISH", "id-1");   // uppercase — mismatch per ADR-008 case-sensitive

        var act = () => sut.DispatchAsync(evt, CancellationToken.None);

        act.Should().ThrowAsync<UnknownEventTypeException>();
    }

    [Fact]
    public void Constructor_NullHandlers_Throws()
    {
        var act = () => new EventDispatcher(handlers: null!);

        act.Should().Throw<ArgumentNullException>();
    }

    private static Mock<IEventHandler> MockHandler(string eventType)
    {
        var mock = new Mock<IEventHandler>();
        mock.SetupGet(h => h.EventType).Returns(eventType);
        mock.Setup(h => h.HandleAsync(It.IsAny<CmsEventEnvelope>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(EventOutcome.Processed());
        return mock;
    }

    private static CmsEventEnvelope NewEvent(string type, string id) => new()
    {
        Type = type,
        Id = id,
        Timestamp = new DateTime(2026, 8, 1, 10, 0, 0, DateTimeKind.Utc),
    };
}
