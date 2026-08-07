namespace CmsEvents.Contracts.Responses;

/// <summary>
/// One entry in <see cref="BatchResponse.Errors"/> — a single event that failed processing.
/// See responses.md § Failure reason enum for valid <see cref="Reason"/> values.
/// </summary>
public sealed class BatchErrorItem
{
    public required int EventIndex { get; init; }

    public required string Id { get; init; }

    public required string Type { get; init; }

    /// <summary>
    /// Machine-readable failure reason. One of:
    /// <c>validation_error</c>, <c>processing_timeout</c>, <c>unknown_event_type</c>.
    /// Internal implementation details are never exposed here (logged separately per ADR-014).
    /// </summary>
    public required string Reason { get; init; }

    /// <summary>
    /// Optional human-readable, non-technical detail intended for the producer.
    /// Must not leak internal implementation.
    /// </summary>
    public string? Detail { get; init; }
}
