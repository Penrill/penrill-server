using Penrill.Application.Boards;
using Penrill.Application.Common.Interfaces;
using Penrill.Application.Common.Security;

namespace Penrill.Application.Sharing.Commands.DisableDrawLink;

/// <summary>Turns the draw link off and takes everyone who came in through it off the board, disconnecting them.</summary>
[Authorize]
public record DisableDrawLinkCommand(string Board) : IRequest;

public class DisableDrawLinkCommandValidator : AbstractValidator<DisableDrawLinkCommand>
{
    public DisableDrawLinkCommandValidator() => RuleFor(c => c.Board).MustBeBoardId();
}

public class DisableDrawLinkCommandHandler(IApplicationDbContext context, IUser user, IPublisher publisher) : IRequestHandler<DisableDrawLinkCommand>
{
    public async Task Handle(DisableDrawLinkCommand request, CancellationToken cancellationToken)
    {
        if (!await context.OwnsAsync(user.Id!, request.Board, cancellationToken)) throw new NotFoundException(request.Board, nameof(Board));
        await using (var transaction = await context.Database.BeginTransactionAsync(cancellationToken))
        {
            await context.BoardPeople.Where(p => p.BoardId == request.Board && p.Via != "").ExecuteDeleteAsync(cancellationToken);
            await context.BoardLinks.Where(l => l.BoardId == request.Board).ExecuteDeleteAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        await publisher.Publish(new BoardAccessRevokedEvent(request.Board), cancellationToken);
    }
}
