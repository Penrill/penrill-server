using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Penrill.Application.Common.Exceptions;
using Penrill.Application.Common.Interfaces;
using Penrill.Application.Common.Security;
using Penrill.Application.Live;
using Penrill.Domain.Boards;

namespace Penrill.Application.Boards.Commands.SubmitChange;

/// <summary>
/// Submits one change over HTTP, for clients without a live connection. Returns the change's sequence number;
/// sending a change again returns the sequence it already got.
/// </summary>
[Authorize]
public record SubmitChangeCommand(string Id, JsonNode? Change) : IRequest<long>;

public class SubmitChangeCommandValidator : AbstractValidator<SubmitChangeCommand>
{
    public SubmitChangeCommandValidator() => RuleFor(c => c.Id).MustBeBoardId();
}

public class SubmitChangeCommandHandler(IApplicationDbContext context, IUser user, RoomRegistry rooms, ILogger<SubmitChangeCommandHandler> logger)
    : IRequestHandler<SubmitChangeCommand, long>
{
    public async Task<long> Handle(SubmitChangeCommand request, CancellationToken cancellationToken)
    {
        var author = user.Id!;
        await context.EnsureAccessAsync(author, request.Id, cancellationToken);
        if (!ChangeCodec.TryParse(request.Change, out var change))
        {
            LiveSessions.ChangeRejected(logger, ChangeCodec.IdOf(request.Change), request.Id, author);
            throw new BadRequestException(ChangeCodec.Invalid);
        }
        var body = JsonSerializer.SerializeToUtf8Bytes(request.Change);
        return await rooms.WithRoom(request.Id, cancellationToken, async room =>
        {
            var result = await room.Submit(change, body, author, cancellationToken);
            return result.Status switch
            {
                SubmitStatus.Accepted or SubmitStatus.Duplicate => result.Seq,
                SubmitStatus.Rejected => throw new BadRequestException(result.Reason),
                _ => throw new ServiceUnavailableException()
            };
        });
    }
}
