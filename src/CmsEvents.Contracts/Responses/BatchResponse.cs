namespace CmsEvents.Contracts.Responses;

/// <summary>
/// Response body for POST /cms/events (200 OK). See responses.md for full contract.
/// </summary>
public sealed class BatchResponse
{
    public required Guid BatchId { get; init; }

    public required Guid CorrelationId { get; init; }

    public required int TotalEvents { get; init; }

    public required int Processed { get; init; }

    public required int Skipped { get; init; }

    public required int Failed { get; init; }

    /// <summary>
    /// Per-event details for failed events only. Skipped events are not itemized in the response
    /// (they appear in structured logs per ADR-014). Empty array when Failed == 0.
    /// </summary>
    public required IReadOnlyList<BatchErrorItem> Errors { get; init; }
}
