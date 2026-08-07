namespace CmsEvents.Contracts.Responses;

using System.Text.Json.Serialization;

/// <summary>
/// Common non-2xx response envelope. See responses.md § Error code reference for valid
/// <see cref="Error"/> values (<c>malformed_batch</c>, <c>unauthorized</c>, <c>forbidden</c>,
/// <c>not_found</c>, <c>rate_limit_exceeded</c>, <c>internal_error</c>).
/// </summary>
public sealed class ErrorEnvelope
{
    public required Guid CorrelationId { get; init; }

    /// <summary>
    /// Present on internal_error responses originating from POST /cms/events per ADR-009.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? BatchId { get; init; }

    public required string Error { get; init; }

    public string? Detail { get; init; }

    /// <summary>
    /// Present on 429 responses per ADR-013.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? RetryAfterSeconds { get; init; }
}
