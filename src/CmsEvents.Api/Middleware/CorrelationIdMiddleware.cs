namespace CmsEvents.Api.Middleware;

using System.Globalization;
using Serilog.Context;

/// <summary>
/// Extracts <c>X-Correlation-ID</c> from the request (or generates one), pushes it into Serilog's
/// LogContext for the request scope, and sets it on the response. See ADR-014.
/// </summary>
public sealed class CorrelationIdMiddleware
{
    public const string HeaderName = "X-Correlation-ID";
    public const string HttpContextItemKey = "CorrelationId";

    private readonly RequestDelegate _next;

    public CorrelationIdMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = ResolveCorrelationId(context);
        context.Items[HttpContextItemKey] = correlationId;
        context.Response.Headers[HeaderName] = correlationId.ToString("D", CultureInfo.InvariantCulture);

        using (LogContext.PushProperty("CorrelationId", correlationId))
        {
            await _next(context);
        }
    }

    private static Guid ResolveCorrelationId(HttpContext context)
    {
        if (context.Request.Headers.TryGetValue(HeaderName, out var headerValue) &&
            Guid.TryParse(headerValue.ToString(), out var parsed))
        {
            return parsed;
        }

        return Guid.NewGuid();
    }
}
