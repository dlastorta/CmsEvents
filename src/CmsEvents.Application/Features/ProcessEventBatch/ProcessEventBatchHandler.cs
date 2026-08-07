namespace CmsEvents.Application.Features.ProcessEventBatch;

using CmsEvents.Application.Common.Repositories;
using CmsEvents.Application.EventProcessing;
using CmsEvents.Contracts.Events;
using CmsEvents.Contracts.Responses;
using CmsEvents.Domain.Abstractions;
using MediatR;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;

/// <summary>
/// Handles <see cref="ProcessEventBatchCommand"/>. Iterates events sequentially per ADR-008,
/// validates each, wraps per-event processing in a transaction, applies Polly retry for transient
/// failures, and aggregates outcomes into a <see cref="BatchResponse"/>.
/// </summary>
public sealed class ProcessEventBatchHandler : IRequestHandler<ProcessEventBatchCommand, BatchResponse>
{
    private const int TransientRetryAttempts = 3;

    /// <summary>
    /// Threshold beyond which an event timestamp triggers a clock-skew Warning log (per ADR-005).
    /// Chosen to accommodate producer timezone misconfiguration (max ~24h across the international
    /// date line) without blocking legitimate events.
    /// </summary>
    private static readonly TimeSpan ClockSkewWarningThreshold = TimeSpan.FromHours(24);

    private static readonly TimeSpan[] RetryDelays =
    {
        TimeSpan.FromMilliseconds(100),
        TimeSpan.FromMilliseconds(200),
        TimeSpan.FromMilliseconds(400),
    };

    private readonly IEntityRepository _repository;
    private readonly EventDispatcher _dispatcher;
    private readonly CmsEventValidator _validator;
    private readonly IClock _clock;
    private readonly ILogger<ProcessEventBatchHandler> _logger;

    public ProcessEventBatchHandler(
        IEntityRepository repository,
        EventDispatcher dispatcher,
        CmsEventValidator validator,
        IClock clock,
        ILogger<ProcessEventBatchHandler> logger)
    {
        _repository = repository;
        _dispatcher = dispatcher;
        _validator = validator;
        _clock = clock;
        _logger = logger;
    }

    public async Task<BatchResponse> Handle(ProcessEventBatchCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var processed = 0;
        var skipped = 0;
        var failed = 0;
        var errors = new List<BatchErrorItem>();

        _logger.LogInformation(
            "Batch received: batchId={BatchId}, totalEvents={Total}",
            request.BatchId, request.Events.Count);

        // Sequential per-event processing per ADR-008.
        for (var i = 0; i < request.Events.Count; i++)
        {
            var evt = request.Events[i];
            var outcome = await ProcessSingleEventAsync(evt, i, cancellationToken);

            switch (outcome.Type)
            {
                case EventOutcomeType.Processed:
                    processed++;
                    break;

                case EventOutcomeType.Skipped:
                    skipped++;
                    break;

                case EventOutcomeType.Failed:
                    failed++;
                    errors.Add(new BatchErrorItem
                    {
                        EventIndex = i,
                        Id = evt.Id,
                        Type = evt.Type,
                        Reason = outcome.Reason!,
                        Detail = outcome.Detail,
                    });
                    break;

                default:
                    throw new InvalidOperationException($"Unknown outcome type: {outcome.Type}");
            }
        }

        _logger.LogInformation(
            "Batch completed: batchId={BatchId}, processed={Processed}, skipped={Skipped}, failed={Failed}",
            request.BatchId, processed, skipped, failed);

        return new BatchResponse
        {
            BatchId = request.BatchId,
            CorrelationId = request.CorrelationId,
            TotalEvents = request.Events.Count,
            Processed = processed,
            Skipped = skipped,
            Failed = failed,
            Errors = errors,
        };
    }

    private async Task<EventOutcome> ProcessSingleEventAsync(
        CmsEventEnvelope evt,
        int index,
        CancellationToken cancellationToken)
    {
        // Validation first — permanent failure on validation error (ADR-008 reason enum).
        var validation = await _validator.ValidateAsync(evt, cancellationToken);
        if (!validation.IsValid)
        {
            var detail = string.Join("; ", validation.Errors.Select(e => e.ErrorMessage));
            _logger.LogWarning(
                "Event {Index} failed validation: id={Id}, detail={Detail}",
                index, evt.Id, detail);
            return EventOutcome.Failed(reason: "validation_error", detail: detail);
        }

        // Clock-skew observability warning per ADR-005 — does NOT alter the idempotency decision;
        // the event is still dispatched normally. Signals producer clock misconfiguration.
        WarnIfClockSkew(evt, index);

        // Transient failure retry per ADR-008.
        var retryPolicy = BuildRetryPolicy();

        try
        {
            var outcome = default(EventOutcome);
            await retryPolicy.ExecuteAsync(async ct =>
            {
                await _repository.ExecuteInTransactionAsync(async innerCt =>
                {
                    outcome = await _dispatcher.DispatchAsync(evt, innerCt);
                }, ct);
            }, cancellationToken);

            return outcome ?? throw new InvalidOperationException("Dispatcher returned null outcome.");
        }
        catch (UnknownEventTypeException ex)
        {
            _logger.LogWarning(ex, "Event {Index} has unknown type: id={Id}, type={Type}", index, evt.Id, evt.Type);
            return EventOutcome.Failed(reason: "unknown_event_type", detail: $"Type '{evt.Type}' is not recognized.");
        }
        catch (Exception ex)
        {
            // Transient retry exhausted — log internal cause (dev-facing) but return generic timeout reason
            // to the producer (ADR-008: no internal implementation leak in response).
            _logger.LogError(ex,
                "Event {Index} processing failed after {Retries} retries: id={Id}",
                index, TransientRetryAttempts, evt.Id);
            return EventOutcome.Failed(
                reason: "processing_timeout",
                detail: "Processing failed, please retry this event");
        }
    }

    private static AsyncRetryPolicy BuildRetryPolicy() => Policy
        .Handle<Exception>(ex => ex is not UnknownEventTypeException)
        .WaitAndRetryAsync(RetryDelays);

    private void WarnIfClockSkew(CmsEventEnvelope evt, int index)
    {
        var now = _clock.UtcNow;
        if (evt.Timestamp > now.Add(ClockSkewWarningThreshold))
        {
            _logger.LogWarning(
                "Event {Index} timestamp is more than {ThresholdHours}h in the future — " +
                "possible clock skew or producer timezone misconfiguration. " +
                "id={Id}, timestamp={Timestamp}, now={Now}",
                index, ClockSkewWarningThreshold.TotalHours, evt.Id, evt.Timestamp, now);
        }
    }
}
