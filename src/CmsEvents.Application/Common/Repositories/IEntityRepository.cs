namespace CmsEvents.Application.Common.Repositories;

using CmsEvents.Domain.Entities;

/// <summary>
/// Write-side abstraction over CMS entity persistence per ADR-010. Used by command handlers.
/// Preserves the Application layer's independence from any specific ORM — implementations
/// live in Infrastructure and use the writer <c>DbContext</c>.
///
/// Transaction management for per-event scope (ADR-008) is exposed via <see cref="ExecuteInTransactionAsync"/>.
/// </summary>
public interface IEntityRepository
{
    /// <summary>
    /// Load an entity by id with change tracking enabled, for load-then-mutate flows.
    /// Returns <c>null</c> if the entity does not exist.
    /// </summary>
    Task<Entity?> FindByIdAsync(string id, CancellationToken cancellationToken = default);

    Task AddAsync(Entity entity, CancellationToken cancellationToken = default);

    void Remove(Entity entity);

    /// <summary>
    /// Commit pending changes to persistence.
    /// </summary>
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Execute the given action inside a database transaction. Commits on successful completion;
    /// rolls back on exception. Used by <c>ProcessEventBatchHandler</c> for per-event transactional
    /// scope per ADR-008.
    /// </summary>
    Task ExecuteInTransactionAsync(
        Func<CancellationToken, Task> action,
        CancellationToken cancellationToken = default);
}
