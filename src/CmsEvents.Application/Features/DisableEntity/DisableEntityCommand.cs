namespace CmsEvents.Application.Features.DisableEntity;

using CmsEvents.Contracts.Responses;
using MediatR;

/// <summary>
/// Command for POST /entities/{id}/disable. Admin-only per ADR-011. Idempotent per ADR-007 —
/// same successful response whether the entity was already disabled or newly disabled.
///
/// Returns null when the entity does not exist (API layer maps to 404).
/// </summary>
public sealed record DisableEntityCommand(string Id, Guid CorrelationId) : IRequest<DisableEnableResponse?>;
