using Penrill.Application.Common.Interfaces;
using Penrill.Application.Common.Security;

namespace Penrill.Application.Boards.Queries.GetBoards;

[Authorize]
public record GetBoardsQuery : IRequest<IReadOnlyList<BoardSummaryDto>>;

public class GetBoardsQueryHandler(IApplicationDbContext context, IUser user) : IRequestHandler<GetBoardsQuery, IReadOnlyList<BoardSummaryDto>>
{
    public async Task<IReadOnlyList<BoardSummaryDto>> Handle(GetBoardsQuery request, CancellationToken cancellationToken)
    {
        var boards = await context.Boards.AsNoTracking()
            .VisibleTo(context, user.Id!)
            .Select(b => new BoardSummaryDto { Id = b.Id, Title = b.Title, Seq = b.Seq, Updated = b.Updated, Owner = b.Owner ?? "" })
            .ToListAsync(cancellationToken);
        return boards.OrderByDescending(b => b.Updated).ThenBy(b => b.Id, StringComparer.Ordinal).ToList();
    }
}
