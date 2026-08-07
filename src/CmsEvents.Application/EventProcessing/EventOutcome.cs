namespace CmsEvents.Application.EventProcessing;

/// <summary>
/// Result of processing a single CMS event. See ADR-008 (outcome semantics) and
/// responses.md for the reason enum values.
/// </summary>
public sealed record EventOutcome(EventOutcomeType Type, string? Reason, string? Detail)
{
    public static EventOutcome Processed() => new(EventOutcomeType.Processed, Reason: null, Detail: null);

    /// <summary>
    /// Not applied for a valid reason (idempotency skip, orphan-delete no-op).
    /// Not surfaced to the producer in the response; visible in logs per ADR-014.
    /// </summary>
    public static EventOutcome Skipped(string reason) => new(EventOutcomeType.Skipped, reason, Detail: null);

    /// <summary>
    /// Not applied due to failure (validation, retry exhaustion, unknown type).
    /// Surfaced to the producer as an error item per responses.md.
    /// </summary>
    public static EventOutcome Failed(string reason, string? detail = null) =>
        new(EventOutcomeType.Failed, reason, detail);
}

public enum EventOutcomeType
{
    Processed = 1,
    Skipped = 2,
    Failed = 3,
}
