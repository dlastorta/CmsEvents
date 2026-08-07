namespace CmsEvents.Application.Features.ProcessEventBatch;

using CmsEvents.Contracts.Events;
using CmsEvents.Contracts.Responses;
using MediatR;

/// <summary>
/// Command for POST /cms/events. The API endpoint constructs it with the parsed batch,
/// the request-scoped correlation ID (from <c>CorrelationIdMiddleware</c>), and a fresh batch ID.
/// See ADR-008 for sync per-event transactional processing behavior.
/// </summary>
public sealed record ProcessEventBatchCommand(
    IReadOnlyList<CmsEventEnvelope> Events,
    Guid CorrelationId,
    Guid BatchId) : IRequest<BatchResponse>;
