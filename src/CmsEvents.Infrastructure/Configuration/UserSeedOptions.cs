namespace CmsEvents.Infrastructure.Configuration;

/// <summary>
/// Bound from the "Users" configuration section (User Secrets in dev, Key Vault in prod per ADR-012).
/// Each entry seeds one row into the Users table if it does not already exist.
///
/// Expected JSON structure:
/// <code>
/// {
///   "Users": {
///     "CmsWebhookUser":  { "Username": "cms-webhook-user", "PasswordHash": "$2a$11$..." },
///     "ReadonlyUser":    { "Username": "readonly-user",    "PasswordHash": "$2a$11$..." },
///     "AdminUser":       { "Username": "admin-user",       "PasswordHash": "$2a$11$..." }
///   }
/// }
/// </code>
/// </summary>
public sealed class UserSeedOptions
{
    public const string SectionName = "Users";

    public UserSeedEntry CmsWebhookUser { get; init; } = new();

    public UserSeedEntry ReadonlyUser { get; init; } = new();

    public UserSeedEntry AdminUser { get; init; } = new();
}

public sealed class UserSeedEntry
{
    public string Username { get; init; } = string.Empty;

    public string PasswordHash { get; init; } = string.Empty;
}
