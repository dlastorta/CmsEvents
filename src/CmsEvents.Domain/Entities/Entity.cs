namespace CmsEvents.Domain.Entities;

using CmsEvents.Domain.Enums;
using CmsEvents.Domain.ValueObjects;

/// <summary>
/// A CMS entity as persisted locally. Owned by CMS webhook events for
/// <see cref="Status"/>, <see cref="LastProcessedVersion"/>,
/// <see cref="LastProcessedTimestamp"/>, and <see cref="Payload"/>; owned by
/// admin API for <see cref="IsDisabled"/>. See ADR-005, ADR-006, ADR-007.
/// </summary>
public sealed class Entity
{
    // Backing constructor for EF Core.
    private Entity() { }

    public string Id { get; private set; } = default!;

    public EntityStatus Status { get; private set; }

    /// <summary>
    /// Local admin override; not touched by CMS event handlers per ADR-007.
    /// </summary>
    public bool IsDisabled { get; private set; }

    public int LastProcessedVersion { get; private set; }

    public DateTime LastProcessedTimestamp { get; private set; }

    /// <summary>
    /// Opaque JSON body from the CMS event, per spec item 2.
    /// </summary>
    public string Payload { get; private set; } = default!;

    public DateTime CreatedAt { get; private set; }

    public DateTime UpdatedAt { get; private set; }

    /// <summary>
    /// Create a new entity from an incoming publish event.
    /// </summary>
    public static Entity CreateFromPublish(string id, int version, DateTime timestamp, string payload, DateTime now)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentException.ThrowIfNullOrEmpty(payload);
        if (version < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(version), "Version must be >= 1.");
        }

        return new Entity
        {
            Id = id,
            Status = EntityStatus.Published,
            IsDisabled = false,
            LastProcessedVersion = version,
            LastProcessedTimestamp = timestamp,
            Payload = payload,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    /// <summary>
    /// Create a new entity from an incoming unpublish event for an entity we have never observed.
    /// Handles the corner case explicitly called out by the spec (per ADR-006).
    /// </summary>
    public static Entity CreateOrphanFromUnpublish(string id, int version, DateTime timestamp, string payload, DateTime now)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentException.ThrowIfNullOrEmpty(payload);
        if (version < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(version), "Version must be >= 1.");
        }

        return new Entity
        {
            Id = id,
            Status = EntityStatus.Unpublished,
            IsDisabled = false,
            LastProcessedVersion = version,
            LastProcessedTimestamp = timestamp,
            Payload = payload,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    /// <summary>
    /// Evaluate an incoming publish or unPublish event against the current state per ADR-005 rule.
    /// </summary>
    public IdempotencyDecision EvaluateForApply(int incomingVersion, DateTime incomingTimestamp)
    {
        if (incomingVersion > LastProcessedVersion)
        {
            return IdempotencyDecision.Apply();
        }

        if (incomingVersion < LastProcessedVersion)
        {
            return IdempotencyDecision.SkipSuperseded();
        }

        // Equal version — tie-break on timestamp.
        if (incomingTimestamp > LastProcessedTimestamp)
        {
            return IdempotencyDecision.Apply();
        }

        return IdempotencyDecision.SkipDuplicate();
    }

    /// <summary>
    /// Evaluate an incoming delete event against the current state per ADR-005 ordering rule.
    /// Delete carries no version, so ordering is derived from timestamp alone: a delete is
    /// applied only if strictly newer than the last observed state. This prevents a late-
    /// arriving delete from erasing a publish/unpublish that already advanced the entity
    /// (network reordering, replayed batch, at-least-once producer).
    /// </summary>
    public IdempotencyDecision EvaluateForDelete(DateTime incomingTimestamp)
    {
        if (incomingTimestamp > LastProcessedTimestamp)
        {
            return IdempotencyDecision.Apply();
        }

        // Equal timestamp is treated as stale — a delete "at the same instant" as the last
        // observed state has no defensible interpretation, so we fail-safe against replays.
        return IdempotencyDecision.SkipStaleDelete();
    }

    /// <summary>
    /// Apply a publish event that has already passed the idempotency check.
    /// </summary>
    public void ApplyPublish(int version, DateTime timestamp, string payload, DateTime now)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);

        Status = EntityStatus.Published;
        LastProcessedVersion = version;
        LastProcessedTimestamp = timestamp;
        Payload = payload;
        UpdatedAt = now;
    }

    /// <summary>
    /// Apply an unpublish event that has already passed the idempotency check.
    /// Does not touch <see cref="IsDisabled"/> (owned by admin API per ADR-007).
    /// </summary>
    public void ApplyUnpublish(int version, DateTime timestamp, string payload, DateTime now)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);

        Status = EntityStatus.Unpublished;
        LastProcessedVersion = version;
        LastProcessedTimestamp = timestamp;
        Payload = payload;
        UpdatedAt = now;
    }

    /// <summary>
    /// Set the local admin-disabled flag. Idempotent — no error if already disabled.
    /// </summary>
    public void Disable(DateTime now)
    {
        if (!IsDisabled)
        {
            IsDisabled = true;
            UpdatedAt = now;
        }
    }

    /// <summary>
    /// Clear the local admin-disabled flag. Idempotent — no error if already enabled.
    /// </summary>
    public void Enable(DateTime now)
    {
        if (IsDisabled)
        {
            IsDisabled = false;
            UpdatedAt = now;
        }
    }
}
