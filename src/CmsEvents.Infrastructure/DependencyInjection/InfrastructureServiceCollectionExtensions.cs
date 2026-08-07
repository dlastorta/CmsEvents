namespace CmsEvents.Infrastructure.DependencyInjection;

using CmsEvents.Application.Common.Repositories;
using CmsEvents.Domain.Abstractions;
using CmsEvents.Infrastructure.Authentication;
using CmsEvents.Infrastructure.Configuration;
using CmsEvents.Infrastructure.Persistence;
using CmsEvents.Infrastructure.Persistence.Repositories;
using CmsEvents.Infrastructure.Time;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SystemClock = CmsEvents.Infrastructure.Time.SystemClock;

/// <summary>
/// Wires up Infrastructure adapters: DbContexts (writer + reader per ADR-010),
/// clock, user seeder, DB initializer, and Basic Authentication (ADR-011).
/// Called by the Api composition root.
/// </summary>
public static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        AddPersistence(services, configuration);
        AddAuthentication(services);
        AddTime(services);

        return services;
    }

    private static void AddPersistence(IServiceCollection services, IConfiguration configuration)
    {
        var writerConnection = configuration.GetConnectionString("Writer")
            ?? throw new InvalidOperationException("Connection string 'Writer' is not configured.");

        var readerConnection = configuration.GetConnectionString("Reader")
            ?? throw new InvalidOperationException("Connection string 'Reader' is not configured.");

        // Note: EnableRetryOnFailure (SqlServerRetryingExecutionStrategy) is intentionally NOT
        // enabled here — it conflicts with user-initiated transactions (BeginTransactionAsync) that
        // ProcessEventBatchHandler uses per ADR-008. Transient-failure retry is handled explicitly
        // by Polly at the handler level; see ADR-008 § Failure classification.
        services.AddDbContext<WriterDbContext>(options =>
            options.UseSqlServer(writerConnection));

        services.AddDbContext<ReaderDbContext>(options =>
            options
                .UseSqlServer(readerConnection)
                .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTrackingWithIdentityResolution));

        // Domain-facing repositories/queries (ADR-010 revision — Application depends on these,
        // not on the concrete DbContexts).
        services.AddScoped<IEntityRepository, EntityRepository>();
        services.AddScoped<IEntityQueries, EntityQueries>();
        services.AddScoped<IUserQueries, UserQueries>();

        // User seed configuration (ADR-011).
        services.Configure<UserSeedOptions>(configuration.GetSection(UserSeedOptions.SectionName));
        services.AddScoped<UserSeeder>();
        services.AddHostedService<DbInitializer>();
    }

    private static void AddAuthentication(IServiceCollection services)
    {
        services
            .AddAuthentication(BasicAuthenticationDefaults.AuthenticationScheme)
            .AddScheme<BasicAuthenticationSchemeOptions, BasicAuthenticationHandler>(
                BasicAuthenticationDefaults.AuthenticationScheme,
                options => { });
    }

    private static void AddTime(IServiceCollection services)
    {
        services.AddSingleton<IClock, SystemClock>();
    }
}
