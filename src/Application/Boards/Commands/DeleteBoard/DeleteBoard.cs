using Microsoft.Extensions.Logging;
using Penrill.Application.Common.Exceptions;
using Penrill.Application.Common.Interfaces;
using Penrill.Application.Common.Security;

namespace Penrill.Application.Boards.Commands.DeleteBoard;

/// <summary>Only the board's owner can delete it; devices still syncing it find it gone.</summary>
[Authorize]
public record DeleteBoardCommand(string Id) : IRequest;

public class DeleteBoardCommandValidator : AbstractValidator<DeleteBoardCommand>
{
    public DeleteBoardCommandValidator() => RuleFor(c => c.Id).MustBeBoardId();
}

public partial class DeleteBoardCommandHandler(IApplicationDbContext context, IUser user, ILogger<DeleteBoardCommandHandler> logger)
    : IRequestHandler<DeleteBoardCommand>
{
    public async Task Handle(DeleteBoardCommand request, CancellationToken cancellationToken)
    {
        var board = await context.Boards.FirstOrDefaultAsync(b => b.Id == request.Id, cancellationToken);
        Guard.Against.NotFound(request.Id, board);
        if (board.Owner != user.Id) throw new ForbiddenAccessException();
        context.Boards.Remove(board);
        board.AddDomainEvent(new BoardDeletedEvent(board.Id));
        await context.SaveChangesAsync(cancellationToken);
        LogDeleted(logger, board.Id, user.Id!);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Deleted board {Board} for {User}")]
    private static partial void LogDeleted(ILogger logger, string board, string user);
}
