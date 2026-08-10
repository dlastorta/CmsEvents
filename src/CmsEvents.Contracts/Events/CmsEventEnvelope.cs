namespace CmsEvents.Contracts.Events;

using System.Globalization;
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
/// - Timestamp is a string in ISO 8601 UTC. Bound as string (not DateTime) so that a single malformed
///   event does not throw during JSON deserialization and break the whole batch — the validator
///   surfaces it as a per-event validation_error per ADR-008 "one bad event should not block valid ones".
/// - Payload is required for publish/unPublish, absent for delete; internal structure is opaque.
/// </summary>
public sealed class CmsEventEnvelope
{
    public string Type { get; init; } = default!;

    public string Id { get; init; } = default!;

    public int? Version { get; init; }

    public string? Timestamp { get; init; }

    public JsonElement? Payload { get; init; }

    /// <summary>
    /// Parses <see cref="Timestamp"/> as UTC. Call ONLY after <c>CmsEventValidator</c> has
    /// confirmed the value is a valid ISO 8601 UTC string — otherwise this throws.
    /// </summary>
    public DateTime GetTimestampUtc()
    {
        if (string.IsNullOrEmpty(Timestamp))
        {
            throw new InvalidOperationException(
                "Timestamp is null or empty. Validator should have rejected this event.");
        }

        return DateTime.Parse(
            Timestamp,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
    }
}
