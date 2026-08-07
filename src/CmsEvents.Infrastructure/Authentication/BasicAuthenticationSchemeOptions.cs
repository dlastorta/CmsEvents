namespace CmsEvents.Infrastructure.Authentication;

using Microsoft.AspNetCore.Authentication;

/// <summary>
/// Options for the Basic Authentication scheme. Currently empty — placeholder for future
/// configuration (e.g., realm name, custom challenge behavior).
/// </summary>
public sealed class BasicAuthenticationSchemeOptions : AuthenticationSchemeOptions
{
    public string Realm { get; set; } = "CmsEvents";
}
