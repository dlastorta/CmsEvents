namespace CmsEvents.Application.Common.Repositories;

using CmsEvents.Domain.Entities;

/// <summary>
/// Read-side abstraction for CMS entity queries per ADR-010. Used by query handlers.
/// Backed by the reader <c>DbContext</c> in Infrastructure — reads may be routed to a read replica
/// in production and use <c>NoTrackingWithIdentityResolution</c> globally.
///
/// The <paramref name="includeHidden"/> parameter controls the role-based visibility filter
/// per ADR-007: pass <c>false</c> for normal users (returns only <c>Status=Published AND IsDisabled=false</c>);
/// pass <c>true</c> for admins (returns everything).
/// </summary>
public interface IEntityQueries
{
    Task<Entity?> FindByIdAsync(string id, bool includeHidden, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Entity>> ListAsync(bool includeHidden, int limit, CancellationToken cancellationToken = default);
}
