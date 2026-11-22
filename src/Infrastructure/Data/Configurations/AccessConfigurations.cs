using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Penrill.Domain.Constants;
using Penrill.Infrastructure.Identity;

namespace Penrill.Infrastructure.Data.Configurations;

public class CredentialConfiguration : IEntityTypeConfiguration<Credential>
{
    public void Configure(EntityTypeBuilder<Credential> builder)
    {
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).HasMaxLength(16);
        builder.Property(c => c.TokenHash).HasMaxLength(64);
        builder.Property(c => c.Device).HasMaxLength(AccountRules.MaxDeviceName);
        builder.HasIndex(c => c.TokenHash).IsUnique();
        builder.HasIndex(c => c.UserId);
        builder.HasIndex(c => c.Expires);
        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(c => c.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class GrantConfiguration : IEntityTypeConfiguration<Grant>
{
    public void Configure(EntityTypeBuilder<Grant> builder)
    {
        builder.HasKey(g => g.Id);
        builder.Property(g => g.Id).HasMaxLength(16);
        builder.Property(g => g.TokenHash).HasMaxLength(64);
        builder.Property(g => g.Subject).HasMaxLength(256);
        builder.Property(g => g.CreatedBy).HasMaxLength(256);
        builder.HasIndex(g => g.TokenHash).IsUnique();
    }
}

public class SettingConfiguration : IEntityTypeConfiguration<Setting>
{
    public void Configure(EntityTypeBuilder<Setting> builder)
    {
        builder.HasKey(s => s.Key);
        builder.Property(s => s.Key).HasMaxLength(64);
    }
}
