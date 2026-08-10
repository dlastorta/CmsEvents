namespace CmsEvents.Application.Common.Exceptions;

/// <summary>
/// Signals a transient failure at the persistence layer (deadlock, brief connection loss, etc.)
/// that the caller MAY retry. Raised by Infrastructure repository implementations to keep
/// Application ORM-agnostic per ADR-010 rule 7 — handlers never catch <c>DbUpdateException</c>
/// directly.
///
/// Distinguished from unhandled exceptions (which bubble up to the global exception handler
/// and become HTTP 500) per ADR-009 § Failure classification.
/// </summary>
public sealed class TransientPersistenceException : Exception
{
    public TransientPersistenceException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
