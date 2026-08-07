namespace CmsEvents.Application.Common.Repositories;

using CmsEvents.Domain.Entities;

/// <summary>
/// Read-side abstraction for user lookups per ADR-011. Used by the Basic Authentication middleware
/// on every request. Backed by the reader <c>DbContext</c> for scale.
/// </summary>
public interface IUserQueries
{
    Task<User?> FindByUsernameAsync(string username, CancellationToken cancellationToken = default);
}
