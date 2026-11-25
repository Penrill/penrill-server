using Application.Boards;
using Application.Common.Interfaces;
using Application.Common.Security;

namespace Application.Sharing.Commands.RemoveBoardPerson;

/// <summary>The owner takes someone off the board; if they are on it right now they are disconnected.</summary>
[Authorize]
public record RemoveBoardPersonCommand(string Board, string Name) : IRequest;

public class RemoveBoardPersonCommandValidator : AbstractValidator<RemoveBoardPersonCommand>
{
    public RemoveBoardPersonCommandValidator() => RuleFor(c => c.Board).MustBeBoardId();
}

public class RemoveBoardPersonCommandHandler(IApplicationDbContext context, IUser user, IPublisher publisher) : IRequestHandler<RemoveBoardPersonCommand>
{
    public async Task Handle(RemoveBoardPersonCommand request, CancellationToken cancellationToken)
    {
        var name = request.Name.Trim().ToLowerInvariant();
        if (!await context.OwnsAsync(user.Id!, request.Board, cancellationToken) ||
            await context.BoardPeople.Where(p => p.BoardId == request.Board && p.UserId == name).ExecuteDeleteAsync(cancellationToken) == 0)
            throw new NotFoundException(request.Board, nameof(Board));
        await publisher.Publish(new BoardAccessRevokedEvent(request.Board), cancellationToken);
    }
}
