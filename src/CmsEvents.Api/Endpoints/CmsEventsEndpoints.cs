namespace CmsEvents.Api.Endpoints;

using System.Globalization;
using CmsEvents.Api.Configuration;
using CmsEvents.Api.Middleware;
using CmsEvents.Application.Features.ProcessEventBatch;
using CmsEvents.Contracts.Events;
using CmsEvents.Contracts.Responses;
using MediatR;
using Serilog.Context;

/// <summary>
/// Endpoint group for POST /cms/events per spec item 1.
/// Response schema in responses.md; per-event processing behavior in ADR-008.
/// </summary>
public static class CmsEventsEndpoints
{
    public static IEndpointRouteBuilder MapCmsEventsEndpoints(this IEndpointRouteBuilder builder)
    {
        var group = builder.MapGroup("/cms/events")
            .RequireAuthorization(AuthorizationPolicies.OrganizationOnly)
            .WithTags("CMS Events");

        group.MapPost("/", async (
            IReadOnlyList<CmsEventEnvelope> events,
            HttpContext context,
            IMediator mediator,
            CancellationToken cancellationToken) =>
        {
            var correlationId = ResolveCorrelationId(context);
            var batchId = Guid.NewGuid();
            // Store batchId in HttpContext so GlobalExceptionHandler can include it in 500 responses.
            context.Items[Middleware.GlobalExceptionHandler.BatchIdContextItem] = batchId;
            context.Response.Headers["X-Batch-Id"] = batchId.ToString("D", CultureInfo.InvariantCulture);

            // Push BatchId into the Serilog LogContext so every log emitted during batch processing
            // (in the handler, validator, dispatcher, per-event handlers, repository) carries it —
            // per ADR-014. CorrelationId is pushed globally by CorrelationIdMiddleware; BatchId is
            // scoped here because it only exists for /cms/events requests.
            using (LogContext.PushProperty("BatchId", batchId))
            {
                var result = await mediator.Send(
                    new ProcessEventBatchCommand(events, correlationId, batchId),
                    cancellationToken);

                return Results.Ok(result);
            }
        })
        .WithName("ProcessEventBatch")
        .Produces<BatchResponse>(StatusCodes.Status200OK)
        .Produces<ErrorEnvelope>(StatusCodes.Status400BadRequest)
        .Produces<ErrorEnvelope>(StatusCodes.Status401Unauthorized)
        .Produces<ErrorEnvelope>(StatusCodes.Status403Forbidden)
        .Produces<ErrorEnvelope>(StatusCodes.Status429TooManyRequests)
        .Produces<ErrorEnvelope>(StatusCodes.Status500InternalServerError)
        // WithRequestTimeout must come after Produces<T> calls — it returns IEndpointConventionBuilder
        // (base type) and Produces<T> requires RouteHandlerBuilder. Chain order matters.
        .WithRequestTimeout(RequestTimeoutPolicies.EventBatch);

        return builder;
    }

    private static Guid ResolveCorrelationId(HttpContext context) =>
        context.Items[CorrelationIdMiddleware.HttpContextItemKey] is Guid id
            ? id
            : Guid.NewGuid();
}
