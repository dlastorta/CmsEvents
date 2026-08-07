namespace CmsEvents.Contracts.Responses;

/// <summary>
/// Response body for POST /entities/{id}/disable and POST /entities/{id}/enable.
/// Idempotent — same response whether the state was already applied or newly applied.
/// </summary>
public sealed class DisableEnableResponse
{
    public required Guid CorrelationId { get; init; }

    public required string Id { get; init; }

    public required bool IsDisabled { get; init; }
}
