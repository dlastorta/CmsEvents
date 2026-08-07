namespace CmsEvents.Application.Features.EnableEntity;

using CmsEvents.Contracts.Responses;
using MediatR;

/// <summary>
/// Command for POST /entities/{id}/enable. Admin-only per ADR-011. Idempotent per ADR-007.
/// Returns null when the entity does not exist (API layer maps to 404).
/// </summary>
public sealed record EnableEntityCommand(string Id, Guid CorrelationId) : IRequest<DisableEnableResponse?>;
