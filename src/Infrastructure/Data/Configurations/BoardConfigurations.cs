using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Domain.Constants;
using Infrastructure.Identity;

namespace Infrastructure.Data.Configurations;

public class BoardConfiguration : IEntityTypeConfiguration<Board>
{
    public void Configure(EntityTypeBuilder<Board> builder)
    {
        builder.HasKey(b => b.Id);
        builder.Property(b => b.Id).HasMaxLength(64);
        builder.Property(b => b.Title).HasMaxLength(ProtocolConstants.MaxTitle);
        builder.Property(b => b.Background).HasMaxLength(ProtocolConstants.MaxBackground);
        builder.Property(b => b.Owner).HasMaxLength(256);
        // A board outlives its owner's account; an administrator can hand it on.
        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(b => b.Owner).OnDelete(DeleteBehavior.SetNull);
        builder.HasIndex(b => b.Owner);
    }
}

public class RemovedBoardConfiguration : IEntityTypeConfiguration<RemovedBoard>
{
    public void Configure(EntityTypeBuilder<RemovedBoard> builder)
    {
        builder.HasKey(b => b.Id);
        builder.Property(b => b.Id).HasMaxLength(64);
    }
}

public class BoardChangeConfiguration : IEntityTypeConfiguration<BoardChange>
{
    public void Configure(EntityTypeBuilder<BoardChange> builder)
    {
        builder.HasKey(c => new { c.BoardId, c.Seq });
        builder.Property(c => c.ChangeId).HasMaxLength(ProtocolConstants.MaxChangeId);
        builder.Property(c => c.Author).HasMaxLength(256);
        builder.HasIndex(c => new { c.BoardId, c.ChangeId }).IsUnique();
        builder.HasOne<Board>().WithMany().HasForeignKey(c => c.BoardId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class BoardAssetConfiguration : IEntityTypeConfiguration<BoardAsset>
{
    public void Configure(EntityTypeBuilder<BoardAsset> builder)
    {
        builder.HasKey(a => new { a.BoardId, a.Hash });
        builder.Property(a => a.Hash).HasMaxLength(64);
        builder.Property(a => a.ContentType).HasMaxLength(64);
        builder.HasOne<Board>().WithMany().HasForeignKey(a => a.BoardId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class BoardPersonConfiguration : IEntityTypeConfiguration<BoardPerson>
{
    public void Configure(EntityTypeBuilder<BoardPerson> builder)
    {
        builder.HasKey(p => new { p.BoardId, p.UserId });
        builder.Property(p => p.Via).HasMaxLength(64);
        builder.HasOne<Board>().WithMany().HasForeignKey(p => p.BoardId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(p => p.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(p => p.UserId);
    }
}

public class BoardLinkConfiguration : IEntityTypeConfiguration<BoardLink>
{
    public void Configure(EntityTypeBuilder<BoardLink> builder)
    {
        builder.HasKey(l => l.BoardId);
        builder.Property(l => l.Token).HasMaxLength(64);
        builder.HasIndex(l => l.Token).IsUnique();
        builder.HasOne<Board>().WithOne().HasForeignKey<BoardLink>(l => l.BoardId).OnDelete(DeleteBehavior.Cascade);
    }
}
