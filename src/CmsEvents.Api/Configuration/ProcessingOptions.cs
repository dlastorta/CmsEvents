namespace CmsEvents.Api.Configuration;

/// <summary>
/// Bound from the "Processing" configuration section. Controls the per-batch processing
/// timeout enforced by <c>AddRequestTimeouts</c> per ADR-009.
/// </summary>
public sealed class ProcessingOptions
{
    public const string SectionName = "Processing";

    /// <summary>
    /// Maximum time (in seconds) allowed for a single POST /cms/events request to complete.
    /// Below Kestrel default (~2 minutes) and typical load balancer defaults. When exceeded,
    /// the request is aborted and the CMS retries per ADR-005 idempotency.
    /// </summary>
    public int TimeoutSeconds { get; init; } = 60;
}
