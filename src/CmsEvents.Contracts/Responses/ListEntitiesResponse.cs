namespace CmsEvents.Contracts.Responses;

/// <summary>
/// Response body for GET /entities.
/// </summary>
public sealed class ListEntitiesResponse
{
    public required Guid CorrelationId { get; init; }

    public required int Count { get; init; }

    public required IReadOnlyList<EntityResponse> Entities { get; init; }
}
