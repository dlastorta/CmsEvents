namespace CmsEvents.Api.Endpoints;

using System.Security.Claims;
using CmsEvents.Api.Configuration;
using CmsEvents.Api.Middleware;
using CmsEvents.Application.Features.DisableEntity;
using CmsEvents.Application.Features.EnableEntity;
using CmsEvents.Application.Features.GetEntity;
using CmsEvents.Application.Features.ListEntities;
using CmsEvents.Contracts.Responses;
using CmsEvents.Domain.Enums;
using MediatR;

/// <summary>
/// Endpoint group for /entities per spec item 4:
/// - GET /entities and GET /entities/{id}: readable by User or Admin, role-filtered per ADR-007.
/// - POST /entities/{id}/disable and /enable: Admin only, idempotent per ADR-007.
/// </summary>
public static class EntitiesEndpoints
{
    private const int DefaultLimit = 100;
    private const int MaxLimit = 500;

    public static IEndpointRouteBuilder MapEntitiesEndpoints(this IEndpointRouteBuilder builder)
    {
        var group = builder.MapGroup("/entities")
            .WithTags("Entities");

        group.MapGet("/", async (
            int? limit,
            HttpContext context,
            IMediator mediator,
            CancellationToken cancellationToken) =>
        {
            var role = ResolveRole(context);
            var effectiveLimit = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
            var correlationId = ResolveCorrelationId(context);

            var result = await mediator.Send(
                new ListEntitiesQuery(role, effectiveLimit, correlationId),
                cancellationToken);

            return Results.Ok(result);
        })
        .RequireAuthorization(AuthorizationPolicies.UserOrAdmin)
        .WithName("ListEntities")
        .Produces<ListEntitiesResponse>(StatusCodes.Status200OK);

        group.MapGet("/{id}", async (
            string id,
            HttpContext context,
            IMediator mediator,
            CancellationToken cancellationToken) =>
        {
            var role = ResolveRole(context);
            var correlationId = ResolveCorrelationId(context);

            var entity = await mediator.Send(new GetEntityQuery(id, role, correlationId), cancellationToken);
            return entity is null
                ? Results.NotFound(BuildNotFoundEnvelope(correlationId))
                : Results.Ok(entity);
        })
        .RequireAuthorization(AuthorizationPolicies.UserOrAdmin)
        .WithName("GetEntity")
        .Produces<EntityResponse>(StatusCodes.Status200OK)
        .Produces<ErrorEnvelope>(StatusCodes.Status404NotFound);

        group.MapPost("/{id}/disable", async (
            string id,
            HttpContext context,
            IMediator mediator,
            CancellationToken cancellationToken) =>
        {
            var correlationId = ResolveCorrelationId(context);
            var result = await mediator.Send(new DisableEntityCommand(id, correlationId), cancellationToken);
            return result is null
                ? Results.NotFound(BuildNotFoundEnvelope(correlationId))
                : Results.Ok(result);
        })
        .RequireAuthorization(AuthorizationPolicies.AdminOnly)
        .WithName("DisableEntity")
        .Produces<DisableEnableResponse>(StatusCodes.Status200OK)
        .Produces<ErrorEnvelope>(StatusCodes.Status404NotFound);

        group.MapPost("/{id}/enable", async (
            string id,
            HttpContext context,
            IMediator mediator,
            CancellationToken cancellationToken) =>
        {
            var correlationId = ResolveCorrelationId(context);
            var result = await mediator.Send(new EnableEntityCommand(id, correlationId), cancellationToken);
            return result is null
                ? Results.NotFound(BuildNotFoundEnvelope(correlationId))
                : Results.Ok(result);
        })
        .RequireAuthorization(AuthorizationPolicies.AdminOnly)
        .WithName("EnableEntity")
        .Produces<DisableEnableResponse>(StatusCodes.Status200OK)
        .Produces<ErrorEnvelope>(StatusCodes.Status404NotFound);

        return builder;
    }

    private static UserRole ResolveRole(HttpContext context)
    {
        var roleClaim = context.User.FindFirstValue(ClaimTypes.Role);
        return Enum.TryParse<UserRole>(roleClaim, ignoreCase: false, out var parsed)
            ? parsed
            : throw new UnauthorizedAccessException("Authenticated user has no valid role claim.");
    }

    private static Guid ResolveCorrelationId(HttpContext context) =>
        context.Items[CorrelationIdMiddleware.HttpContextItemKey] is Guid id
            ? id
            : Guid.NewGuid();

    private static ErrorEnvelope BuildNotFoundEnvelope(Guid correlationId) => new()
    {
        CorrelationId = correlationId,
        Error = "not_found",
        Detail = "Entity not found",
    };
}
