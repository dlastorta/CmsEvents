namespace CmsEvents.Domain.Entities;

using CmsEvents.Domain.Enums;

/// <summary>
/// An authenticated user for Basic Authentication per ADR-011.
/// Seeded at startup from configuration; passwords are BCrypt-hashed.
/// </summary>
public sealed class User
{
    // Backing constructor for EF Core.
    private User() { }

    public Guid Id { get; private set; }

    public string Username { get; private set; } = default!;

    public string PasswordHash { get; private set; } = default!;

    public UserRole Role { get; private set; }

    public DateTime CreatedAt { get; private set; }

    /// <summary>
    /// Create a new user from seed configuration.
    /// </summary>
    public static User Create(string username, string passwordHash, UserRole role, DateTime now)
    {
        ArgumentException.ThrowIfNullOrEmpty(username);
        ArgumentException.ThrowIfNullOrEmpty(passwordHash);
        if (username.Length is < 10 or > 20)
        {
            throw new ArgumentException("Username must be 10-20 characters per spec item 1.", nameof(username));
        }

        return new User
        {
            Id = Guid.NewGuid(),
            Username = username,
            PasswordHash = passwordHash,
            Role = role,
            CreatedAt = now,
        };
    }

    /// <summary>
    /// Update the password hash. Used during 90-day rotation (see runbook-secret-rotation.md).
    /// </summary>
    public void UpdatePasswordHash(string newPasswordHash)
    {
        ArgumentException.ThrowIfNullOrEmpty(newPasswordHash);
        PasswordHash = newPasswordHash;
    }

    /// <summary>
    /// Convenience: check whether this user holds the given role. String-based check preserves
    /// migration path to full RBAC per ADR-011 discussion.
    /// </summary>
    public bool HasRole(UserRole role) => Role == role;
}
