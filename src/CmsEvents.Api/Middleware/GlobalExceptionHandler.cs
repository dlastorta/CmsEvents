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

        // Client-error exceptions produced by ASP.NET Core during request binding (malformed
        // JSON body, missing required field types, oversized body, etc.) surface here as
        // BadHttpRequestException. Those are 4xx by nature — surfacing them as 500 would
        // misclassify a caller mistake as a server bug and confuse observability. Preserve
        // the framework-provided status code, wrap the response in the same ErrorEnvelope
        // shape for consistency.
        if (exception is BadHttpRequestException badRequest)
        {
            var statusCode = badRequest.StatusCode > 0
                ? badRequest.StatusCode
                : StatusCodes.Status400BadRequest;

            _logger.LogWarning(
                exception,
                "Client error — returning {StatusCode}. correlationId={CorrelationId}, path={Path}",
                statusCode, correlationId, httpContext.Request.Path.Value);

            httpContext.Response.StatusCode = statusCode;
            httpContext.Response.ContentType = "application/json";

            await httpContext.Response
                .WriteAsJsonAsync(
                    new ErrorEnvelope
                    {
                        CorrelationId = correlationId,
                        BatchId = batchId,
                        Error = "malformed_batch",
                        Detail = "Request body could not be parsed. See correlationId in server logs.",
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            return true;
        }

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
