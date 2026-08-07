namespace CmsEvents.Application.Features.GetEntity;

using CmsEvents.Contracts.Responses;
using CmsEvents.Domain.Enums;
using MediatR;

/// <summary>
/// Query for GET /entities/{id}. Returns null when the entity does not exist OR is filtered out
/// by role (see ADR-007 for anti-enumeration rationale — the API layer returns 404 in both cases).
/// </summary>
public sealed record GetEntityQuery(string Id, UserRole Role, Guid CorrelationId) : IRequest<EntityResponse?>;
