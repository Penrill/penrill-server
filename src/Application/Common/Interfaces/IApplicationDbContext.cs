using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Penrill.Application.Common.Interfaces;

/// <summary>The data the use cases work with (ADR-001). Accounts themselves live behind <see cref="IIdentityService"/>.</summary>
public interface IApplicationDbContext
{
    DbSet<Board> Boards { get; }

    DbSet<BoardChange> BoardChanges { get; }

    DbSet<BoardAsset> BoardAssets { get; }

    DbSet<BoardPerson> BoardPeople { get; }

    DbSet<BoardLink> BoardLinks { get; }

    DbSet<Folder> Folders { get; }

    DbSet<FolderMember> FolderMembers { get; }

    DbSet<FolderBoard> FolderBoards { get; }

    DbSet<Template> Templates { get; }

    DbSet<Credential> Credentials { get; }

    DbSet<Grant> Grants { get; }

    DbSet<Setting> Settings { get; }

    /// <summary>For transactions around multi-step writes such as appending to a board's change log.</summary>
    DatabaseFacade Database { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken);
}
