using Microsoft.Extensions.Logging;
using Application.Common.Exceptions;
using Application.Common.Interfaces;
using Application.Common.Security;

namespace Application.Boards.Commands.DeleteBoard;

/// <summary>
/// Only the board's owner can delete it; devices still syncing it find it gone. With <paramref name="RemoveCopies"/>
/// the devices of the people it was shared with also drop their copy, otherwise they keep it as their own.
/// </summary>
[Authorize]
public record DeleteBoardCommand(string Id, bool RemoveCopies = false) : IRequest;

public class DeleteBoardCommandValidator : AbstractValidator<DeleteBoardCommand>
{
    public DeleteBoardCommandValidator() => RuleFor(c => c.Id).MustBeBoardId();
}

public partial class DeleteBoardCommandHandler(IApplicationDbContext context, IUser user, TimeProvider time, ILogger<DeleteBoardCommandHandler> logger)
    : IRequestHandler<DeleteBoardCommand>
{
    public async Task Handle(DeleteBoardCommand request, CancellationToken cancellationToken)
    {
        var board = await context.Boards.FirstOrDefaultAsync(b => b.Id == request.Id, cancellationToken);
        Guard.Against.NotFound(request.Id, board);
        if (board.Owner != user.Id) throw new ForbiddenAccessException();
        context.Boards.Remove(board);
        if (request.RemoveCopies && !await context.RemovedBoards.AnyAsync(b => b.Id == board.Id, cancellationToken))
            context.RemovedBoards.Add(new RemovedBoard { Id = board.Id, At = time.GetUtcNow() });
        board.AddDomainEvent(new BoardDeletedEvent(board.Id));
        await context.SaveChangesAsync(cancellationToken);
        LogDeleted(logger, board.Id, user.Id!);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Deleted board {Board} for {User}")]
    private static partial void LogDeleted(ILogger logger, string board, string user);
}
