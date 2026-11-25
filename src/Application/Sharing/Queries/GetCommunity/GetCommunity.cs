using Application.Common.Interfaces;
using Application.Common.Security;

namespace Application.Sharing.Queries.GetCommunity;

/// <summary>The user's folders, who is in them, the boards shared into them and the templates the user can add.</summary>
[Authorize]
public record GetCommunityQuery : IRequest<CommunityDto>;

public sealed record CommunityDto(
    IReadOnlyList<FolderDto> Folders, IReadOnlyList<MemberDto> Members, IReadOnlyList<FolderBoardDto> Boards, IReadOnlyList<TemplateDto> Templates);

public sealed record FolderDto(string Id, string Name, string Owner);

public sealed record MemberDto(string Folder, string Name);

public sealed record FolderBoardDto(string Folder, string Id, string Title, long Seq, DateTimeOffset Updated);

/// <summary>A shared template. <c>Updated</c> (Unix milliseconds) changes whenever its author edits it, so copies can follow.</summary>
public sealed record TemplateDto(string Id, string Name, string Folder, string Author, long Updated);

public class GetCommunityQueryHandler(IApplicationDbContext context, IUser user) : IRequestHandler<GetCommunityQuery, CommunityDto>
{
    public async Task<CommunityDto> Handle(GetCommunityQuery request, CancellationToken cancellationToken)
    {
        var me = user.Id!;
        var mine = context.FolderMembers.Where(m => m.UserId == me).Select(m => m.FolderId);
        var folders = await context.Folders.AsNoTracking().Where(f => mine.Contains(f.Id))
            .Select(f => new FolderDto(f.Id, f.Name, f.Owner)).ToListAsync(cancellationToken);
        var members = await context.FolderMembers.AsNoTracking().Where(m => mine.Contains(m.FolderId))
            .Select(m => new MemberDto(m.FolderId, m.UserId)).ToListAsync(cancellationToken);
        var boards = await (from share in context.FolderBoards
                            join board in context.Boards on share.BoardId equals board.Id
                            where mine.Contains(share.FolderId)
                            select new FolderBoardDto(share.FolderId, board.Id, board.Title, board.Seq, board.Updated))
            .AsNoTracking().ToListAsync(cancellationToken);
        var templates = await context.Templates.AsNoTracking().Where(t => t.Folder == "" || mine.Contains(t.Folder))
            .Select(t => new TemplateDto(t.Id, t.Name, t.Folder, t.Author, t.Updated)).ToListAsync(cancellationToken);
        return new CommunityDto(
            folders.OrderBy(f => f.Name, StringComparer.Ordinal).ToList(),
            members.OrderBy(m => m.Name, StringComparer.Ordinal).ToList(),
            boards.OrderBy(b => b.Title, StringComparer.Ordinal).ToList(),
            templates.OrderBy(t => t.Name, StringComparer.Ordinal).ToList());
    }
}
