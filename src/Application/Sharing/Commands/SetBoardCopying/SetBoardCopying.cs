using Penrill.Application.Boards;
using Penrill.Application.Common.Exceptions;
using Penrill.Application.Common.Interfaces;
using Penrill.Application.Common.Security;

namespace Penrill.Application.Sharing.Commands.SetBoardCopying;

/// <summary>The owner lets the people a board is shared with duplicate, export and copy it out, or stops them.</summary>
[Authorize]
public record SetBoardCopyingCommand(string Board, bool? Allowed) : IRequest;

public class SetBoardCopyingCommandValidator : AbstractValidator<SetBoardCopyingCommand>
{
    public SetBoardCopyingCommandValidator()
    {
        RuleFor(c => c.Board).MustBeBoardId();
        RuleFor(c => c.Allowed).NotNull();
    }
}

public class SetBoardCopyingCommandHandler(IApplicationDbContext context, IUser user) : IRequestHandler<SetBoardCopyingCommand>
{
    public async Task Handle(SetBoardCopyingCommand request, CancellationToken cancellationToken)
    {
        var board = await context.Boards.FirstOrDefaultAsync(b => b.Id == request.Board, cancellationToken);
        Guard.Against.NotFound(request.Board, board);
        if (board.Owner != user.Id) throw new ForbiddenAccessException();
        board.Copying = request.Allowed!.Value;
        await context.SaveChangesAsync(cancellationToken);
    }
}
