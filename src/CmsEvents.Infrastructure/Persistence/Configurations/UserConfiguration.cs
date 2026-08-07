namespace CmsEvents.Infrastructure.Persistence.Configurations;

using CmsEvents.Domain.Entities;
using CmsEvents.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

/// <summary>
/// EF Core mapping for <see cref="User"/> per ADR-011.
/// Role is persisted as string via HasConversion for readable DB queries.
/// </summary>
internal sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("Users");

        builder.HasKey(u => u.Id);

        builder.Property(u => u.Username)
            .HasMaxLength(20)
            .IsRequired();

        builder.HasIndex(u => u.Username)
            .IsUnique()
            .HasDatabaseName("IX_Users_Username");

        builder.Property(u => u.PasswordHash)
            .HasMaxLength(60)  // BCrypt hash length
            .IsRequired();

        builder.Property(u => u.Role)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(u => u.CreatedAt).IsRequired();
    }
}
