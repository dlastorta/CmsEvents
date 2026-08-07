namespace CmsEvents.Domain.ValueObjects;

/// <summary>
/// Outcome of evaluating an incoming publish/unPublish event against the
/// currently persisted entity, per the idempotency rule in ADR-005.
/// </summary>
public readonly record struct IdempotencyDecision
{
    public IdempotencyOutcome Outcome { get; }

    /// <summary>
    /// Machine-readable skip reason. Non-null only when <see cref="Outcome"/> is <see cref="IdempotencyOutcome.Skip"/>.
    /// Values: <c>superseded_by_version</c>, <c>duplicate</c>.
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
}

public enum IdempotencyOutcome
{
    Apply = 1,
    Skip = 2,
}
