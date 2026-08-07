namespace CmsEvents.Api.Configuration;

using CmsEvents.Domain.Enums;
using Microsoft.AspNetCore.Authorization;

/// <summary>
/// Authorization policy names and configuration per ADR-011.
/// String-based role checks preserve migration path to full RBAC.
/// </summary>
public static class AuthorizationPolicies
{
    public const string OrganizationOnly = nameof(OrganizationOnly);
    public const string UserOrAdmin = nameof(UserOrAdmin);
    public const string AdminOnly = nameof(AdminOnly);

    public static void Configure(AuthorizationOptions options)
    {
        options.AddPolicy(OrganizationOnly, p => p.RequireRole(UserRole.Organization.ToString()));
        options.AddPolicy(UserOrAdmin, p => p.RequireRole(UserRole.User.ToString(), UserRole.Admin.ToString()));
        options.AddPolicy(AdminOnly, p => p.RequireRole(UserRole.Admin.ToString()));
    }
}
