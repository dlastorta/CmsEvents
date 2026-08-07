namespace CmsEvents.Api.Configuration;

using System.Globalization;
using System.Threading.RateLimiting;
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
            options.OnRejected = SetRetryAfterHeader;
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

    private static ValueTask SetRetryAfterHeader(OnRejectedContext context, CancellationToken _)
    {
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            context.HttpContext.Response.Headers.RetryAfter =
                ((int)retryAfter.TotalSeconds).ToString(CultureInfo.InvariantCulture);
        }

        return ValueTask.CompletedTask;
    }

    private static string BuildPartitionKey(HttpContext context)
    {
        var user = context.User.Identity?.Name ?? "anonymous";
        var path = context.Request.Path.Value ?? string.Empty;
        return $"{user}:{path}";
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
