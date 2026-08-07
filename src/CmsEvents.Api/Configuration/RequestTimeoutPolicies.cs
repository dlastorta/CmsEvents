namespace CmsEvents.Api.Configuration;

/// <summary>
/// Named request-timeout policies registered via <c>AddRequestTimeouts</c> per ADR-009.
/// Endpoints apply them via <c>.WithRequestTimeout(RequestTimeoutPolicies.PolicyName)</c>.
/// </summary>
public static class RequestTimeoutPolicies
{
    /// <summary>
    /// Timeout for POST /cms/events. Duration bound from <see cref="ProcessingOptions.TimeoutSeconds"/>.
    /// </summary>
    public const string EventBatch = nameof(EventBatch);
}
