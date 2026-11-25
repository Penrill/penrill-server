using Application.Common.Interfaces;
using Infrastructure.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;

namespace Infrastructure.Data;

/// <summary>The Penrill Server model on PostgreSQL.</summary>
public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
    : IdentityDbContext<ApplicationUser>(options), IApplicationDbContext
{
    public DbSet<Board> Boards => Set<Board>();

    public DbSet<BoardChange> BoardChanges => Set<BoardChange>();

    public DbSet<BoardAsset> BoardAssets => Set<BoardAsset>();

    public DbSet<BoardPerson> BoardPeople => Set<BoardPerson>();

    public DbSet<BoardLink> BoardLinks => Set<BoardLink>();

    public DbSet<RemovedBoard> RemovedBoards => Set<RemovedBoard>();

    public DbSet<Folder> Folders => Set<Folder>();

    public DbSet<FolderMember> FolderMembers => Set<FolderMember>();

    public DbSet<FolderBoard> FolderBoards => Set<FolderBoard>();

    public DbSet<Template> Templates => Set<Template>();

    public DbSet<Credential> Credentials => Set<Credential>();

    public DbSet<Grant> Grants => Set<Grant>();

    public DbSet<Setting> Settings => Set<Setting>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.ApplyConfigurationsFromAssembly(typeof(ApplicationDbContext).Assembly);
    }
}
