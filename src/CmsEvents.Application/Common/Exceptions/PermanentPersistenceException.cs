namespace CmsEvents.Application.Common.Exceptions;

/// <summary>
/// Signals a NON-transient failure at the persistence layer — e.g., primary-key violation,
/// unique-index violation, foreign-key / check constraint failure, NULL-in-non-null column,
/// string truncation, or an optimistic-concurrency conflict.
///
/// Raised by Infrastructure repository implementations to keep Application ORM-agnostic per
/// ADR-010 rule 7 — handlers never catch <c>DbUpdateException</c> directly. Distinct from
/// <see cref="TransientPersistenceException"/> so that Polly retry policies handle only the
/// truly retryable failures; retrying a constraint violation just delays the same error.
///
/// The optional <see cref="SqlErrorNumber"/> carries the underlying SQL Server error code for
/// observability (logs), never surfaced in the producer-facing response per ADR-011.
/// </summary>
public sealed class PermanentPersistenceException : Exception
{
    public int? SqlErrorNumber { get; }

    public PermanentPersistenceException(string message, int? sqlErrorNumber, Exception innerException)
        : base(message, innerException)
    {
        SqlErrorNumber = sqlErrorNumber;
    }
}
