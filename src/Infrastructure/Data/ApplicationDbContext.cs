using System.Reflection;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Penrill.Application.Common.Interfaces;
using Penrill.Infrastructure.Identity;

namespace Penrill.Infrastructure.Data;

/// <summary>
/// The Penrill model, the same for every host. The database provider and the assembly holding its migrations come
/// from the host: SQLite for a self-hosted server, PostgreSQL for Penrill Cloud. A host that keeps data of its own
/// (Penrill Cloud's audit log, for one) derives from this context and adds its sets.
/// </summary>
public class ApplicationDbContext : IdentityDbContext<ApplicationUser>, IApplicationDbContext
{
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : base(options) { }

    /// <summary>For contexts that derive from this one.</summary>
    protected ApplicationDbContext(DbContextOptions options) : base(options) { }

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

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        base.ConfigureConventions(configurationBuilder);
        // SQLite has no date type: stored as text, DateTimeOffset values cannot be compared or ordered in SQL.
        // As UTC ticks they can.
        if (Database.ProviderName == "Microsoft.EntityFrameworkCore.Sqlite")
            configurationBuilder.Properties<DateTimeOffset>().HaveConversion<DateTimeOffsetToBinaryConverter>();
    }
}
