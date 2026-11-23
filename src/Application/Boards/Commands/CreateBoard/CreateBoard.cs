using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Penrill.Application.Common.Exceptions;
using Penrill.Application.Common.Interfaces;
using Penrill.Application.Common.Security;
using Penrill.Application.Live;
using Penrill.Domain.Boards;

namespace Penrill.Application.Boards.Commands.CreateBoard;

/// <summary>Uploads a board made on a device. Sending it again is safe: the board that already exists answers with its sequence.</summary>
[Authorize]
public record CreateBoardCommand(string Id, JsonNode? Content) : IRequest<CreateBoardResult>;

public sealed record CreateBoardResult(long Seq, bool Created);

public class CreateBoardCommandValidator : AbstractValidator<CreateBoardCommand>
{
    public CreateBoardCommandValidator() => RuleFor(c => c.Id).MustBeBoardId();
}

public partial class CreateBoardCommandHandler(
    IApplicationDbContext context, IUser user, RoomRegistry rooms, TimeProvider time, ILogger<CreateBoardCommandHandler> logger)
    : IRequestHandler<CreateBoardCommand, CreateBoardResult>
{
    public async Task<CreateBoardResult> Handle(CreateBoardCommand request, CancellationToken cancellationToken)
    {
        if (!BoardContent.TryParse(request.Content, out var content)) throw new BadRequestException();
        var owner = user.Id!;
        var guest = user.Roles?.Contains(nameof(Role.Guest)) == true;
        if (!guest && !await context.Boards.AnyAsync(b => b.Id == request.Id, cancellationToken))
        {
            var now = time.GetUtcNow();
            context.Boards.Add(new Board
            {
                Id = request.Id, Owner = owner, Title = content.Title, Background = content.Background,
                Snapshot = content.SerializeItems(), Created = now, Updated = now
            });
            // The id is in use again: whoever still finds it now finds a board they may or may not open.
            await context.RemovedBoards.Where(b => b.Id == request.Id).ExecuteDeleteAsync(cancellationToken);
            try
            {
                await context.SaveChangesAsync(cancellationToken);
                LogCreated(logger, request.Id, owner, content.Items.Count);
                return new CreateBoardResult(0, true);
            }
            catch (DbUpdateException)
            {
                // Another device of the same person uploaded it a moment earlier: fall through to the existing board.
            }
        }
        if (await context.AccessAsync(owner, request.Id, cancellationToken) != BoardAccess.Allowed)
            throw guest ? new ForbiddenAccessException() : new ConflictException();
        return await rooms.WithRoom(request.Id, cancellationToken, room => Task.FromResult(new CreateBoardResult(room.Seq, false)));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Created board {Board} for {User} with {Items} items")]
    private static partial void LogCreated(ILogger logger, string board, string user, int items);
}
