using Penrill.Application.Boards;
using Penrill.Application.Common.Interfaces;
using Penrill.Application.Common.Security;

namespace Penrill.Application.Sharing.Commands.AddBoardPerson;

/// <summary>The owner lets someone onto the board by name. Not found when the board isn't theirs or nobody has that name.</summary>
[Authorize]
public record AddBoardPersonCommand(string Board, string? Name) : IRequest;

public class AddBoardPersonCommandValidator : AbstractValidator<AddBoardPersonCommand>
{
    public AddBoardPersonCommandValidator()
    {
        RuleFor(c => c.Board).MustBeBoardId();
        RuleFor(c => c.Name).Must(BoardLimits.IsLabel);
    }
}

public class AddBoardPersonCommandHandler(IApplicationDbContext context, IUser user, IIdentityService identity) : IRequestHandler<AddBoardPersonCommand>
{
    public async Task Handle(AddBoardPersonCommand request, CancellationToken cancellationToken)
    {
        var owner = user.Id!;
        var name = request.Name!.Trim().ToLowerInvariant();
        if (name == owner || !await context.OwnsAsync(owner, request.Board, cancellationToken) || await identity.FindAsync(name) is null)
            throw new NotFoundException(request.Board, nameof(Board));
        if (await context.BoardPeople.AnyAsync(p => p.BoardId == request.Board && p.UserId == name, cancellationToken)) return;
        context.BoardPeople.Add(new BoardPerson { BoardId = request.Board, UserId = name });
        await context.SaveChangesAsync(cancellationToken);
    }
}
