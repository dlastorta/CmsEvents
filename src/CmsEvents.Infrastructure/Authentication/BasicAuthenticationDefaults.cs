namespace CmsEvents.Infrastructure.Authentication;

/// <summary>
/// Constants used by the Basic Authentication scheme (per ADR-011).
/// </summary>
public static class BasicAuthenticationDefaults
{
    public const string SchemeName = "Basic";

    public const string AuthorizationHeaderName = "Authorization";

    public const string AuthenticationScheme = "Basic";
}
