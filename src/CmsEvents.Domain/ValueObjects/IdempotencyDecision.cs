namespace CmsEvents.Domain.ValueObjects;

/// <summary>
/// Outcome of evaluating an incoming publish/unPublish/delete event against the
/// currently persisted entity, per the idempotency + ordering rule in ADR-005.
/// </summary>
public readonly record struct IdempotencyDecision
{
    public IdempotencyOutcome Outcome { get; }

    /// <summary>
    /// Machine-readable skip reason. Non-null only when <see cref="Outcome"/> is <see cref="IdempotencyOutcome.Skip"/>.
    /// Values: <c>superseded_by_version</c>, <c>duplicate</c>, <c>stale_delete</c>.
    /// </summary>
    public string? SkipReason { get; }

    private IdempotencyDecision(IdempotencyOutcome outcome, string? skipReason)
    {
        Outcome = outcome;
        SkipReason = skipReason;
    }

    public static IdempotencyDecision Apply() => new(IdempotencyOutcome.Apply, skipReason: null);

    public static IdempotencyDecision SkipSuperseded() => new(IdempotencyOutcome.Skip, "superseded_by_version");

    public static IdempotencyDecision SkipDuplicate() => new(IdempotencyOutcome.Skip, "duplicate");

    /// <summary>
    /// Delete event arrived out-of-order: its timestamp is not strictly newer than the last
    /// state we observed (a later publish or unpublish already advanced the entity). Applying
    /// would erase valid state. Emitted by <see cref="Entity.EvaluateForDelete(DateTime)"/>.
    /// </summary>
    public static IdempotencyDecision SkipStaleDelete() => new(IdempotencyOutcome.Skip, "stale_delete");
}

public enum IdempotencyOutcome
{
    Apply = 1,
    Skip = 2,
}
