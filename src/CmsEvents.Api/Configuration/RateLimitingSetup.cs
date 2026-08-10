namespace CmsEvents.Api.Configuration;

using System.Globalization;
using System.Threading.RateLimiting;
using CmsEvents.Api.Middleware;
using CmsEvents.Contracts.Responses;
using Microsoft.AspNetCore.RateLimiting;

/// <summary>
/// Rate-limiting registration extracted from <c>Program.cs</c> to keep individual
/// method complexity low. See ADR-013 for algorithm, partition strategy, and default limits.
/// </summary>
public static class RateLimitingSetup
{
    public static IServiceCollection AddRateLimitingPolicies(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var limits = RateLimitBounds.FromConfiguration(configuration);

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = WriteRejectionResponseAsync;
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
                RateLimitPartition.GetSlidingWindowLimiter(
                    partitionKey: BuildPartitionKey(context),
                    factory: _ => new SlidingWindowRateLimiterOptions
                    {
                        PermitLimit = ResolvePermitLimit(context, limits),
                        Window = TimeSpan.FromMinutes(1),
                        SegmentsPerWindow = 6,
                        QueueLimit = 0,
                    }));
        });

        return services;
    }

    /// <summary>
    /// Fallback Retry-After when the limiter does not emit metadata. Matches the sliding-window
    /// segment duration (Window 60s / 6 segments = 10s per segment) — the minimum time before
    /// the oldest segment expires and one permit becomes available.
    /// </summary>
    private const int DefaultRetryAfterSeconds = 10;

    /// <summary>
    /// Writes the 429 response body per <c>responses.md</c> (correlationId + error + retryAfterSeconds)
    /// and sets the <c>Retry-After</c> header. Called by the rate limiter on rejection.
    ///
    /// The sliding-window limiter with <c>QueueLimit=0</c> does not always emit
    /// <see cref="MetadataName.RetryAfter"/> metadata — falls back to the segment duration to
    /// guarantee the producer always sees an actionable hint.
    /// </summary>
    private static async ValueTask WriteRejectionResponseAsync(OnRejectedContext context, CancellationToken cancellationToken)
    {
        var retryAfterSeconds = DefaultRetryAfterSeconds;
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            retryAfterSeconds = Math.Max((int)retryAfter.TotalSeconds, 1);
        }

        context.HttpContext.Response.Headers["Retry-After"] =
            retryAfterSeconds.ToString(CultureInfo.InvariantCulture);

        var correlationId = context.HttpContext.Items[CorrelationIdMiddleware.HttpContextItemKey] is Guid id
            ? id
            : Guid.NewGuid();

        var envelope = new ErrorEnvelope
        {
            CorrelationId = correlationId,
            Error = "rate_limit_exceeded",
            RetryAfterSeconds = retryAfterSeconds,
        };

        context.HttpContext.Response.ContentType = "application/json";
        await context.HttpContext.Response
            .WriteAsJsonAsync(envelope, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Per ADR-013 (revised): partition key is <c>user:{username}:{path}</c> for authenticated
    /// requests — per-user per-endpoint bucket, so a burst on /cms/events does not exhaust a
    /// user's ability to query /entities. Falls back to <c>ip:{remote}</c> for the edge case where
    /// an unauthenticated request reaches the limiter (should not happen — auth middleware runs first).
    /// </summary>
    private static string BuildPartitionKey(HttpContext context)
    {
        var user = context.User.Identity?.Name;
        if (!string.IsNullOrEmpty(user))
        {
            var path = context.Request.Path.Value ?? string.Empty;
            return $"user:{user}:{path}";
        }

        var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return $"ip:{ip}";
    }

    private static int ResolvePermitLimit(HttpContext context, RateLimitBounds limits)
    {
        var path = context.Request.Path.Value ?? string.Empty;

        if (path.StartsWith("/cms/events", StringComparison.OrdinalIgnoreCase))
        {
            return limits.CmsEventsPerMinute;
        }

        if (path.StartsWith("/entities", StringComparison.OrdinalIgnoreCase))
        {
            return HttpMethods.IsPost(context.Request.Method)
                ? limits.AdminActionsPerMinute
                : limits.QueryPerMinute;
        }

        return limits.QueryPerMinute;
    }

    private sealed record RateLimitBounds(int CmsEventsPerMinute, int QueryPerMinute, int AdminActionsPerMinute)
    {
        public static RateLimitBounds FromConfiguration(IConfiguration configuration) => new(
            CmsEventsPerMinute: configuration.GetValue("RateLimiting:CmsEventsPerMinute", 100),
            QueryPerMinute: configuration.GetValue("RateLimiting:QueryPerMinute", 60),
            AdminActionsPerMinute: configuration.GetValue("RateLimiting:AdminActionsPerMinute", 30));
    }
}
