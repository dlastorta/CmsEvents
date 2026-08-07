namespace CmsEvents.Contracts.Events;

using System.Text.Json;

/// <summary>
/// Wire format of an incoming CMS event, per spec item 1 sample.
///
/// Sample:
/// <code>
/// { "type": "publish", "id": "X", "payload": { ... }, "version": 2, "timestamp": "2026-08-01T10:00:00Z" }
/// { "type": "delete", "id": "Y", "timestamp": "2026-08-01T10:00:00Z" }
/// { "type": "unPublish", "id": "Z", "payload": { ... }, "version": 4, "timestamp": "2026-08-01T10:00:00Z" }
/// </code>
///
/// Constraints per ADR-008:
/// - Type is case-sensitive, one of: publish, unPublish, delete.
/// - Version is required for publish/unPublish, absent for delete; when present, must be &gt;= 1.
/// - Payload is required for publish/unPublish, absent for delete; internal structure is opaque.
/// </summary>
public sealed class CmsEventEnvelope
{
    public string Type { get; init; } = default!;

    public string Id { get; init; } = default!;

    public int? Version { get; init; }

    public DateTime Timestamp { get; init; }

    public JsonElement? Payload { get; init; }
}
