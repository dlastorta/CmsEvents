namespace CmsEvents.Infrastructure.Persistence;

using System.Reflection;
using CmsEvents.Domain.Entities;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Writer-side EF Core context per ADR-010. Owns migrations and per-event transactions (ADR-008).
/// Full change tracking enabled by default. Registered as Scoped in DI.
///
/// Consumed only by Infrastructure adapters (EntityRepository, UserSeeder). Handlers depend on
/// the domain-facing repositories in Application, not on this type — see ADR-010 revision on
/// repository pattern.
/// </summary>
public sealed class WriterDbContext : DbContext
{
    public WriterDbContext(DbContextOptions<WriterDbContext> options)
        : base(options)
    {
    }

    public DbSet<Entity> Entities => Set<Entity>();

    public DbSet<User> Users => Set<User>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(Assembly.GetExecutingAssembly());

        base.OnModelCreating(modelBuilder);
    }
}
