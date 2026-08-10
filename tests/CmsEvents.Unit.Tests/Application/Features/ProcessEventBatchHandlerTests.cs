namespace CmsEvents.Unit.Tests.Application.Features;

using System.Text.Json;
using CmsEvents.Application.Common.Exceptions;
using CmsEvents.Application.Common.Repositories;
using CmsEvents.Application.EventProcessing;
using CmsEvents.Application.Features.ProcessEventBatch;
using CmsEvents.Contracts.Events;
using CmsEvents.Unit.Tests.TestDoubles;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

/// <summary>
/// Covers the batch orchestration in <see cref="ProcessEventBatchHandler"/> — validation,
/// per-event dispatch, outcome aggregation, retry, unknown-type wrapping, and the ADR-005
/// clock-skew warning path.
/// </summary>
public sealed class ProcessEventBatchHandlerTests
{
    private static readonly DateTime Now = new(2026, 8, 1, 10, 0, 0, DateTimeKind.Utc);
    private const string NowIso = "2026-08-01T10:00:00Z";
    private static readonly JsonElement Payload = JsonDocument.Parse("{}").RootElement;

    [Fact]
    public async Task Handle_AllProcessed_ReturnsCountsAndEmptyErrors()
    {
        var repository = new Mock<IEntityRepository>();
        SetupPassthroughTransaction(repository);
        var dispatcher = DispatcherReturning(EventOutcome.Processed(), EventOutcome.Processed());

        var sut = NewHandler(repository, dispatcher);

        var command = NewCommand(
            NewValidPublish("a", version: 1),
            NewValidPublish("b", version: 1));

        var response = await sut.Handle(command, CancellationToken.None);

        response.TotalEvents.Should().Be(2);
        response.Processed.Should().Be(2);
        response.Skipped.Should().Be(0);
        response.Failed.Should().Be(0);
        response.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_MixedOutcomes_AggregatesCorrectly()
    {
        var repository = new Mock<IEntityRepository>();
        SetupPassthroughTransaction(repository);
        var dispatcher = DispatcherReturning(
            EventOutcome.Processed(),
            EventOutcome.Skipped("duplicate"),
            EventOutcome.Skipped("superseded_by_version"));

        var sut = NewHandler(repository, dispatcher);

        var command = NewCommand(
            NewValidPublish("a", version: 1),
            NewValidPublish("b", version: 1),
            NewValidPublish("c", version: 1));

        var response = await sut.Handle(command, CancellationToken.None);

        response.Processed.Should().Be(1);
        response.Skipped.Should().Be(2);
        response.Failed.Should().Be(0);
        response.Errors.Should().BeEmpty("skipped events are logs-only per ADR-008");
    }

    [Fact]
    public async Task Handle_ValidationError_YieldsFailedOutcomeWithValidationReason()
    {
        var repository = new Mock<IEntityRepository>();
        SetupPassthroughTransaction(repository);
        var dispatcher = DispatcherReturning(EventOutcome.Processed());

        var sut = NewHandler(repository, dispatcher);

        // publish event missing version → validation_error
        var invalid = new CmsEventEnvelope
        {
            Type = CmsEventType.Publish,
            Id = "no-version",
            Version = null,
            Timestamp = NowIso,
            Payload = Payload,
        };

        var command = NewCommand(invalid);
        var response = await sut.Handle(command, CancellationToken.None);

        response.Failed.Should().Be(1);
        response.Errors.Should().ContainSingle(e =>
            e.EventIndex == 0 &&
            e.Id == "no-version" &&
            e.Type == CmsEventType.Publish &&
            e.Reason == "validation_error");
    }

    [Fact]
    public async Task Handle_UnknownEventType_MapsToUnknownEventTypeReason()
    {
        var repository = new Mock<IEntityRepository>();
        SetupPassthroughTransaction(repository);

        var dispatcherMock = new Mock<IEventHandler>();
        dispatcherMock.SetupGet(h => h.EventType).Returns(CmsEventType.Publish);
        var dispatcher = new EventDispatcher(new[] { dispatcherMock.Object });

        var sut = NewHandler(repository, dispatcher);

        // Bypass validator by using a type not in the enum — validator will fail first with
        // validation_error, so to exercise unknown_event_type we need a type that passes validation
        // (impossible with our validator) OR test via a direct dispatcher error path.
        // Since validator rejects any unknown type, this scenario is only reachable if validator
        // is bypassed. Skip in favor of the direct EventDispatcher test in EventDispatcherTests.
        var command = NewCommand(NewValidPublish("a", version: 1));

        dispatcherMock
            .Setup(h => h.HandleAsync(It.IsAny<CmsEventEnvelope>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new UnknownEventTypeException("simulated-unmapped-type"));

        var response = await sut.Handle(command, CancellationToken.None);

        response.Failed.Should().Be(1);
        response.Errors.Should().ContainSingle(e => e.Reason == "unknown_event_type");
    }

    [Fact]
    public async Task Handle_TransientFailure_RetriesThenMapsToProcessingTimeout()
    {
        var repository = new Mock<IEntityRepository>();
        SetupPassthroughTransaction(repository);

        var handlerMock = new Mock<IEventHandler>();
        handlerMock.SetupGet(h => h.EventType).Returns(CmsEventType.Publish);
        handlerMock
            .Setup(h => h.HandleAsync(It.IsAny<CmsEventEnvelope>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TransientPersistenceException(
                "simulated transient DB error",
                new InvalidOperationException("underlying")));

        var dispatcher = new EventDispatcher(new[] { handlerMock.Object });
        var sut = NewHandler(repository, dispatcher);

        var command = NewCommand(NewValidPublish("a", version: 1));

        var response = await sut.Handle(command, CancellationToken.None);

        response.Failed.Should().Be(1);
        response.Errors.Should().ContainSingle(e =>
            e.Reason == "processing_timeout" &&
            e.Detail == "Processing failed, please retry this event");

        // Called 4 times total: 1 initial + 3 retries (Polly WaitAndRetryAsync with 3 delays)
        handlerMock.Verify(h => h.HandleAsync(It.IsAny<CmsEventEnvelope>(), It.IsAny<CancellationToken>()), Times.Exactly(4));
    }

    [Fact]
    public async Task Handle_FarFutureTimestamp_ProcessesEventNormally_ClockSkewIsPureObservability()
    {
        var repository = new Mock<IEntityRepository>();
        SetupPassthroughTransaction(repository);
        var dispatcher = DispatcherReturning(EventOutcome.Processed());

        var clock = new FakeClock(Now);
        var sut = NewHandler(repository, dispatcher, clock: clock);

        // > 24h threshold — should trigger clock-skew Warning but not affect outcome.
        var evt = new CmsEventEnvelope
        {
            Type = CmsEventType.Publish,
            Id = "clock-skew",
            Version = 1,
            Timestamp = "2026-09-01T10:00:00Z",
            Payload = Payload,
        };

        var response = await sut.Handle(NewCommand(evt), CancellationToken.None);

        // The clock-skew warning is a Warning log — the event is still processed successfully.
        response.Processed.Should().Be(1);
        response.Failed.Should().Be(0);
    }

    private static ProcessEventBatchHandler NewHandler(
        Mock<IEntityRepository> repository,
        EventDispatcher dispatcher,
        FakeClock? clock = null) => new(
            repository.Object,
            dispatcher,
            new CmsEventValidator(),
            clock ?? new FakeClock(Now),
            NullLogger<ProcessEventBatchHandler>.Instance);

    private static ProcessEventBatchCommand NewCommand(params CmsEventEnvelope[] events) =>
        new(events, Guid.NewGuid(), Guid.NewGuid());

    private static CmsEventEnvelope NewValidPublish(string id, int version) => new()
    {
        Type = CmsEventType.Publish,
        Id = id,
        Version = version,
        Timestamp = NowIso,
        Payload = Payload,
    };

    /// <summary>
    /// Wires the repository mock so <c>ExecuteInTransactionAsync</c> just runs the action.
    /// </summary>
    private static void SetupPassthroughTransaction(Mock<IEntityRepository> repository)
    {
        repository
            .Setup(r => r.ExecuteInTransactionAsync(It.IsAny<Func<CancellationToken, Task>>(), It.IsAny<CancellationToken>()))
            .Returns((Func<CancellationToken, Task> action, CancellationToken ct) => action(ct));
    }

    /// <summary>
    /// Builds a dispatcher that returns the supplied outcomes in order (one per event).
    /// </summary>
    private static EventDispatcher DispatcherReturning(params EventOutcome[] outcomes)
    {
        var queue = new Queue<EventOutcome>(outcomes);
        var handler = new Mock<IEventHandler>();
        handler.SetupGet(h => h.EventType).Returns(CmsEventType.Publish);
        handler
            .Setup(h => h.HandleAsync(It.IsAny<CmsEventEnvelope>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => queue.Dequeue());
        return new EventDispatcher(new[] { handler.Object });
    }
}
