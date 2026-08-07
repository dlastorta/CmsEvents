namespace CmsEvents.Domain.Enums;

/// <summary>
/// Role assigned to an authenticated user per ADR-011.
/// Persisted as string via HasConversion in the EF Core mapping.
/// </summary>
public enum UserRole
{
    /// <summary>
    /// CMS webhook caller. Authorized on POST /cms/events only.
    /// </summary>
    Organization = 1,

    /// <summary>
    /// Read-only consumer. Authorized on GET /entities and /entities/{id}.
    /// Sees only Published entities that are not admin-disabled.
    /// </summary>
    User = 2,

    /// <summary>
    /// Full-access administrator. Sees all entities regardless of Status/IsDisabled
    /// and can invoke POST /entities/{id}/disable and /enable.
    /// </summary>
    Admin = 3,
}
