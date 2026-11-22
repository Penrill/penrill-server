using Penrill.Application.Common.Interfaces;
using Penrill.Application.Common.Security;
using Penrill.Application.Live;

namespace Penrill.Application.Boards.Queries.GetBoardSnapshot;

/// <summary>The whole board as JSON: <c>{id, seq, title, background, items}</c>.</summary>
[Authorize]
public record GetBoardSnapshotQuery(string Id) : IRequest<byte[]>;

public class GetBoardSnapshotQueryValidator : AbstractValidator<GetBoardSnapshotQuery>
{
    public GetBoardSnapshotQueryValidator() => RuleFor(q => q.Id).MustBeBoardId();
}

public class GetBoardSnapshotQueryHandler(IApplicationDbContext context, IUser user, RoomRegistry rooms) : IRequestHandler<GetBoardSnapshotQuery, byte[]>
{
    public async Task<byte[]> Handle(GetBoardSnapshotQuery request, CancellationToken cancellationToken)
    {
        await context.EnsureAccessAsync(user.Id!, request.Id, cancellationToken);
        return await rooms.WithRoom(request.Id, cancellationToken, room => room.Snapshot(cancellationToken));
    }
}
