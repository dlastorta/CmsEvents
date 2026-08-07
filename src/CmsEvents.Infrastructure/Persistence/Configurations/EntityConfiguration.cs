namespace CmsEvents.Infrastructure.Persistence.Configurations;

using CmsEvents.Domain.Entities;
using CmsEvents.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

/// <summary>
/// EF Core mapping for <see cref="Entity"/> per ADR-007 (state model) and ADR-010 (persistence).
/// Composite index on (Status, IsDisabled) covers the normal user query per architecture.md.
/// </summary>
internal sealed class EntityConfiguration : IEntityTypeConfiguration<Entity>
{
    public void Configure(EntityTypeBuilder<Entity> builder)
    {
        builder.ToTable("Entities");

        builder.HasKey(e => e.Id);

        builder.Property(e => e.Id)
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(e => e.Status)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(e => e.IsDisabled)
            .IsRequired();

        builder.Property(e => e.LastProcessedVersion)
            .IsRequired();

        builder.Property(e => e.LastProcessedTimestamp)
            .IsRequired();

        builder.Property(e => e.Payload)
            .HasColumnType("nvarchar(max)")
            .IsRequired();

        builder.Property(e => e.CreatedAt).IsRequired();
        builder.Property(e => e.UpdatedAt).IsRequired();

        // Composite index for the normal-user query predicate (Status = Published AND IsDisabled = false).
        builder.HasIndex(e => new { e.Status, e.IsDisabled })
            .HasDatabaseName("IX_Entities_Status_IsDisabled");

        // Payload is valid JSON at the storage level — enforced via a CHECK constraint
        // (CK_Entities_Payload_IsJson) added by the InitialSchema migration.
    }
}
