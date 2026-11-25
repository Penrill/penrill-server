using Application.Common.Exceptions;
using Application.Common.Interfaces;
using Application.Common.Security;
using Sync.Live;

namespace Application.Boards.Queries.GetBoardChanges;

/// <summary>
/// The changes after <see cref="After"/> as JSON <c>{seq, changes}</c>. Gone when the board no longer keeps them:
/// the client then takes a snapshot instead.
/// </summary>
[Authorize]
public record GetBoardChangesQuery(string Id, long After) : IRequest<byte[]>;

public class GetBoardChangesQueryValidator : AbstractValidator<GetBoardChangesQuery>
{
    public GetBoardChangesQueryValidator() => RuleFor(q => q.Id).MustBeBoardId();
}

public class GetBoardChangesQueryHandler(IApplicationDbContext context, IUser user, RoomRegistry rooms) : IRequestHandler<GetBoardChangesQuery, byte[]>
{
    public async Task<byte[]> Handle(GetBoardChangesQuery request, CancellationToken cancellationToken)
    {
        await context.EnsureAccessAsync(user.Id!, request.Id, cancellationToken);
        return await rooms.WithRoom(request.Id, cancellationToken, async room =>
            await room.ChangesAfter(request.After, cancellationToken) ?? throw new GoneException());
    }
}
