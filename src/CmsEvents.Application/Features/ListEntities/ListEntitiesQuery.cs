namespace CmsEvents.Application.Features.ListEntities;

using CmsEvents.Contracts.Responses;
using CmsEvents.Domain.Enums;
using MediatR;

/// <summary>
/// Query for GET /entities. Role-aware filter per ADR-007 — normal users see only
/// <c>Status=Published AND IsDisabled=false</c>; admins see everything.
/// </summary>
public sealed record ListEntitiesQuery(UserRole Role, int Limit, Guid CorrelationId) : IRequest<ListEntitiesResponse>;
