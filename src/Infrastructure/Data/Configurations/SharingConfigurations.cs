using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Domain.Constants;
using Infrastructure.Identity;

namespace Infrastructure.Data.Configurations;

public class FolderConfiguration : IEntityTypeConfiguration<Folder>
{
    public void Configure(EntityTypeBuilder<Folder> builder)
    {
        builder.HasKey(f => f.Id);
        builder.Property(f => f.Id).HasMaxLength(32);
        builder.Property(f => f.Name).HasMaxLength(ProtocolConstants.MaxLabel);
        builder.Property(f => f.Owner).HasMaxLength(256);
    }
}

public class FolderMemberConfiguration : IEntityTypeConfiguration<FolderMember>
{
    public void Configure(EntityTypeBuilder<FolderMember> builder)
    {
        builder.HasKey(m => new { m.FolderId, m.UserId });
        builder.HasOne<Folder>().WithMany().HasForeignKey(m => m.FolderId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(m => m.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(m => m.UserId);
    }
}

public class FolderBoardConfiguration : IEntityTypeConfiguration<FolderBoard>
{
    public void Configure(EntityTypeBuilder<FolderBoard> builder)
    {
        builder.HasKey(s => new { s.FolderId, s.BoardId });
        builder.HasOne<Folder>().WithMany().HasForeignKey(s => s.FolderId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Board>().WithMany().HasForeignKey(s => s.BoardId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(s => s.BoardId);
    }
}

public class TemplateConfiguration : IEntityTypeConfiguration<Template>
{
    public void Configure(EntityTypeBuilder<Template> builder)
    {
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).HasMaxLength(32);
        builder.Property(t => t.Folder).HasMaxLength(32);
        builder.Property(t => t.Author).HasMaxLength(256);
        builder.Property(t => t.Name).HasMaxLength(ProtocolConstants.MaxLabel);
        builder.HasIndex(t => t.Folder);
    }
}
