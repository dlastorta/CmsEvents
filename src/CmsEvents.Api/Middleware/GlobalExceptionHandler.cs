namespace CmsEvents.Api.Middleware;

using System.Globalization;
using CmsEvents.Contracts.Responses;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.Extensions.Logging;

/// <summary>
/// Global fallback for unhandled exceptions per ADR-009 (catastrophic failure).
/// Returns HTTP 500 with an <see cref="ErrorEnvelope"/> containing <c>correlationId</c> and,
/// when the exception occurred inside a batch request, <c>batchId</c> — enabling support
/// workflows to trace the failure.
///
/// This does NOT catch per-event failures — those are handled explicitly by
/// <c>ProcessEventBatchHandler</c> and become <c>outcome: "failed"</c> items in the batch response.
/// Only system-wide failures (DB down, unexpected exceptions escaping the retry policy) reach here.
/// </summary>
public sealed class GlobalExceptionHandler : IExceptionHandler
{
    public const string BatchIdContextItem = "BatchId";

    private readonly ILogger<GlobalExceptionHandler> _logger;

    public GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger)
    {
        _logger = logger;
    }

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var correlationId = ResolveCorrelationId(httpContext);
        var batchId = ResolveBatchId(httpContext);

        _logger.LogError(
            exception,
            "Unhandled exception — returning 500. correlationId={CorrelationId}, batchId={BatchId}, path={Path}",
            correlationId, batchId?.ToString() ?? "(none)", httpContext.Request.Path.Value);

        httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;
        httpContext.Response.ContentType = "application/json";

        var envelope = new ErrorEnvelope
        {
            CorrelationId = correlationId,
            BatchId = batchId,
            Error = "internal_error",
            Detail = "Contact support with batchId and correlationId",
        };

        await httpContext.Response
            .WriteAsJsonAsync(envelope, cancellationToken)
            .ConfigureAwait(false);

        return true;
    }

    private static Guid ResolveCorrelationId(HttpContext context) =>
        context.Items[CorrelationIdMiddleware.HttpContextItemKey] is Guid id
            ? id
            : Guid.NewGuid();

    private static Guid? ResolveBatchId(HttpContext context) =>
        context.Items[BatchIdContextItem] is Guid id
            ? id
            : null;
}
