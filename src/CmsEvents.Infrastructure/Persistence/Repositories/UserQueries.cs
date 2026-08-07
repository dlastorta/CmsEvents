namespace CmsEvents.Infrastructure.Persistence.Repositories;

using CmsEvents.Application.Common.Repositories;
using CmsEvents.Domain.Entities;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// EF Core implementation of <see cref="IUserQueries"/> per ADR-011. Used by the Basic Authentication
/// middleware on every request. Uses <see cref="ReaderDbContext"/> for scale (read replica in production).
/// </summary>
public sealed class UserQueries : IUserQueries
{
    private readonly ReaderDbContext _dbContext;

    public UserQueries(ReaderDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public Task<User?> FindByUsernameAsync(string username, CancellationToken cancellationToken = default) =>
        _dbContext.Users.FirstOrDefaultAsync(u => u.Username == username, cancellationToken);
}
