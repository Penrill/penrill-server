using Penrill.Application.Common.Interfaces;
using Penrill.Application.Common.Security;

namespace Penrill.Application.Boards.Queries.GetBoards;

[Authorize]
public record GetBoardsQuery : IRequest<IReadOnlyList<BoardSummaryDto>>;

public class GetBoardsQueryHandler(IApplicationDbContext context, IUser user) : IRequestHandler<GetBoardsQuery, IReadOnlyList<BoardSummaryDto>>
{
    public async Task<IReadOnlyList<BoardSummaryDto>> Handle(GetBoardsQuery request, CancellationToken cancellationToken)
    {
        var me = user.Id!;
        var boards = await context.Boards.AsNoTracking()
            .VisibleTo(context, me)
            .Select(b => new BoardSummaryDto
            {
                Id = b.Id, Title = b.Title, Seq = b.Seq, Updated = b.Updated, Owner = b.Owner ?? "",
                Copying = b.Copying,
                Shared = b.Owner == me && (context.BoardPeople.Any(p => p.BoardId == b.Id)
                    || context.BoardLinks.Any(l => l.BoardId == b.Id)
                    || context.FolderBoards.Any(f => f.BoardId == b.Id))
            })
            .ToListAsync(cancellationToken);
        return boards.OrderByDescending(b => b.Updated).ThenBy(b => b.Id, StringComparer.Ordinal).ToList();
    }
}
