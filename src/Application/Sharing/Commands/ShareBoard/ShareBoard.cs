using Penrill.Application.Boards;
using Penrill.Application.Common.Exceptions;
using Penrill.Application.Common.Interfaces;
using Penrill.Application.Common.Security;

namespace Penrill.Application.Sharing.Commands.ShareBoard;

/// <summary>The board's owner shares it into a folder they are in. A conflict when either is not theirs.</summary>
[Authorize(Policy = Policies.NoGuests)]
public record ShareBoardCommand(string? Folder, string? Board) : IRequest;

public class ShareBoardCommandValidator : AbstractValidator<ShareBoardCommand>
{
    public ShareBoardCommandValidator()
    {
        RuleFor(c => c.Folder).NotEmpty();
        RuleFor(c => c.Board).Must(BoardLimits.IsBoardId);
    }
}

public class ShareBoardCommandHandler(IApplicationDbContext context, IUser user) : IRequestHandler<ShareBoardCommand>
{
    public async Task Handle(ShareBoardCommand request, CancellationToken cancellationToken)
    {
        var me = user.Id!;
        if (!await context.FolderMembers.AnyAsync(m => m.FolderId == request.Folder && m.UserId == me, cancellationToken) ||
            !await context.OwnsAsync(me, request.Board!, cancellationToken))
            throw new ConflictException();
        if (await context.FolderBoards.AnyAsync(s => s.FolderId == request.Folder && s.BoardId == request.Board, cancellationToken)) return;
        context.FolderBoards.Add(new FolderBoard { FolderId = request.Folder!, BoardId = request.Board! });
        await context.SaveChangesAsync(cancellationToken);
    }
}
