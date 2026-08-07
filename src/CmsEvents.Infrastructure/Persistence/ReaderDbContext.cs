namespace CmsEvents.Infrastructure.Persistence;

using System.Reflection;
using CmsEvents.Domain.Entities;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Reader-side EF Core context per ADR-010. No tracking globally; connection string in production
/// points to a read replica. In dev, uses ApplicationIntent=ReadOnly against the same DB as writer.
/// Does not own migrations.
///
/// Consumed only by Infrastructure adapters (EntityQueries, UserQueries). Handlers depend on the
/// domain-facing query interfaces in Application, not on this type.
/// </summary>
public sealed class ReaderDbContext : DbContext
{
    public ReaderDbContext(DbContextOptions<ReaderDbContext> options)
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

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder.UseQueryTrackingBehavior(QueryTrackingBehavior.NoTrackingWithIdentityResolution);

        base.OnConfiguring(optionsBuilder);
    }
}
