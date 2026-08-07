namespace CmsEvents.Contracts.Responses;

using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// Response body for GET /entities/{id} and each item in <see cref="ListEntitiesResponse.Entities"/>.
///
/// Role-aware shape (per ADR-007): <see cref="Status"/> and <see cref="IsDisabled"/> are populated only for
/// Admin responses. The API layer sets them to null when serializing for a User, and the
/// <see cref="JsonIgnoreCondition.WhenWritingNull"/> attribute omits them from the wire.
/// </summary>
public sealed class EntityResponse
{
    public required string Id { get; init; }

    public required int Version { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Status { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsDisabled { get; init; }

    public required DateTime Timestamp { get; init; }

    public required JsonElement Payload { get; init; }
}
