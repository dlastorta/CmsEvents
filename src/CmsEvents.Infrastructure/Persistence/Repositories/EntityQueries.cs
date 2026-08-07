namespace CmsEvents.Infrastructure.Persistence.Repositories;

using CmsEvents.Application.Common.Repositories;
using CmsEvents.Domain.Entities;
using CmsEvents.Domain.Enums;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// EF Core implementation of <see cref="IEntityQueries"/> per ADR-010. Uses <see cref="ReaderDbContext"/>
/// (NoTracking globally; connection may target a read replica in production).
/// </summary>
public sealed class EntityQueries : IEntityQueries
{
    private readonly ReaderDbContext _dbContext;

    public EntityQueries(ReaderDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public Task<Entity?> FindByIdAsync(
        string id,
        bool includeHidden,
        CancellationToken cancellationToken = default)
    {
        IQueryable<Entity> query = _dbContext.Entities.Where(e => e.Id == id);

        if (!includeHidden)
        {
            query = query.Where(e => e.Status == EntityStatus.Published && !e.IsDisabled);
        }

        return query.FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Entity>> ListAsync(
        bool includeHidden,
        int limit,
        CancellationToken cancellationToken = default)
    {
        IQueryable<Entity> query = _dbContext.Entities;

        if (!includeHidden)
        {
            query = query.Where(e => e.Status == EntityStatus.Published && !e.IsDisabled);
        }

        return await query
            .OrderBy(e => e.Id)
            .Take(limit)
            .ToListAsync(cancellationToken);
    }
}
