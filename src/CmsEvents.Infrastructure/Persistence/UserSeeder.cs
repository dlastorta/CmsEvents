namespace CmsEvents.Infrastructure.Persistence;

using CmsEvents.Domain.Abstractions;
using CmsEvents.Domain.Entities;
using CmsEvents.Domain.Enums;
using CmsEvents.Infrastructure.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Seeds the three required users (organization, user, admin) at startup per ADR-011.
/// Idempotent — updates the password hash if it drifted from configuration, inserts if missing.
/// </summary>
public sealed class UserSeeder
{
    private readonly WriterDbContext _dbContext;
    private readonly UserSeedOptions _options;
    private readonly IClock _clock;
    private readonly ILogger<UserSeeder> _logger;

    public UserSeeder(
        WriterDbContext dbContext,
        IOptions<UserSeedOptions> options,
        IClock clock,
        ILogger<UserSeeder> logger)
    {
        _dbContext = dbContext;
        _options = options.Value;
        _clock = clock;
        _logger = logger;
    }

    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        var seeds = new[]
        {
            (_options.CmsWebhookUser, UserRole.Organization),
            (_options.ReadonlyUser, UserRole.User),
            (_options.AdminUser, UserRole.Admin),
        };

        foreach (var (entry, role) in seeds)
        {
            if (string.IsNullOrWhiteSpace(entry.Username) || string.IsNullOrWhiteSpace(entry.PasswordHash))
            {
                _logger.LogWarning("Seed entry for role {Role} is not configured; skipping.", role);
                continue;
            }

            var existing = await _dbContext.Users
                .FirstOrDefaultAsync(u => u.Username == entry.Username, cancellationToken);

            if (existing is null)
            {
                var user = User.Create(entry.Username, entry.PasswordHash, role, _clock.UtcNow);
                await _dbContext.Users.AddAsync(user, cancellationToken);
                _logger.LogInformation("Seeded user {Username} with role {Role}.", entry.Username, role);
            }
            else if (existing.PasswordHash != entry.PasswordHash)
            {
                existing.UpdatePasswordHash(entry.PasswordHash);
                _logger.LogInformation("Updated password hash for user {Username} (rotation applied).", entry.Username);
            }
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }
}
