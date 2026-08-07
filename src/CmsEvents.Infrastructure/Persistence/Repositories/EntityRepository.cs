namespace CmsEvents.Infrastructure.Persistence.Repositories;

using CmsEvents.Application.Common.Repositories;
using CmsEvents.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

/// <summary>
/// EF Core implementation of <see cref="IEntityRepository"/> per ADR-010. Uses <see cref="WriterDbContext"/>.
/// </summary>
public sealed class EntityRepository : IEntityRepository
{
    private readonly WriterDbContext _dbContext;

    public EntityRepository(WriterDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public Task<Entity?> FindByIdAsync(string id, CancellationToken cancellationToken = default) =>
        _dbContext.Entities.FirstOrDefaultAsync(e => e.Id == id, cancellationToken);

    public async Task AddAsync(Entity entity, CancellationToken cancellationToken = default) =>
        await _dbContext.Entities.AddAsync(entity, cancellationToken);

    public void Remove(Entity entity) => _dbContext.Entities.Remove(entity);

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
        _dbContext.SaveChangesAsync(cancellationToken);

    public async Task ExecuteInTransactionAsync(
        Func<CancellationToken, Task> action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);

        // If an ambient transaction already exists (e.g., test fixture), reuse it.
        if (_dbContext.Database.CurrentTransaction is not null)
        {
            await action(cancellationToken);
            return;
        }

        // Use CreateExecutionStrategy so this works whether the DbContext is configured with a
        // retrying strategy (SqlServerRetryingExecutionStrategy via EnableRetryOnFailure) or the
        // default non-retrying strategy. Without this wrapper, user-initiated transactions throw
        // when a retrying strategy is active. See:
        // https://learn.microsoft.com/ef/core/miscellaneous/connection-resiliency#execution-strategies-and-transactions
        var strategy = _dbContext.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async ct =>
        {
            await using IDbContextTransaction transaction =
                await _dbContext.Database.BeginTransactionAsync(ct);

            try
            {
                await action(ct);
                await transaction.CommitAsync(ct);
            }
            catch
            {
                await transaction.RollbackAsync(ct);
                throw;
            }
        }, cancellationToken);
    }
}
