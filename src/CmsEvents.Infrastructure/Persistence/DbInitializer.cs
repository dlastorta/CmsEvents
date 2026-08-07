namespace CmsEvents.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>
/// Runs pending EF Core migrations and seeds users at startup. Registered as IHostedService.
/// </summary>
public sealed class DbInitializer : IHostedService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<DbInitializer> _logger;

    public DbInitializer(
        IServiceProvider serviceProvider,
        IHostEnvironment environment,
        ILogger<DbInitializer> logger)
    {
        _serviceProvider = serviceProvider;
        _environment = environment;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<WriterDbContext>();

        _logger.LogInformation("Applying pending migrations...");
        await dbContext.Database.MigrateAsync(cancellationToken);
        _logger.LogInformation("Migrations applied.");

        var seeder = scope.ServiceProvider.GetRequiredService<UserSeeder>();
        _logger.LogInformation("Seeding users from configuration...");
        await seeder.SeedAsync(cancellationToken);
        _logger.LogInformation("User seeding completed.");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
