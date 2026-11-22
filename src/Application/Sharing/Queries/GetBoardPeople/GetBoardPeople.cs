using Penrill.Application.Boards;
using Penrill.Application.Common.Interfaces;
using Penrill.Application.Common.Security;

namespace Penrill.Application.Sharing.Queries.GetBoardPeople;

/// <summary>Who the owner let onto the board, by name. Not found for anyone but the owner.</summary>
[Authorize]
public record GetBoardPeopleQuery(string Board) : IRequest<IReadOnlyList<string>>;

public class GetBoardPeopleQueryValidator : AbstractValidator<GetBoardPeopleQuery>
{
    public GetBoardPeopleQueryValidator() => RuleFor(q => q.Board).MustBeBoardId();
}

public class GetBoardPeopleQueryHandler(IApplicationDbContext context, IUser user) : IRequestHandler<GetBoardPeopleQuery, IReadOnlyList<string>>
{
    public async Task<IReadOnlyList<string>> Handle(GetBoardPeopleQuery request, CancellationToken cancellationToken)
    {
        if (!await context.OwnsAsync(user.Id!, request.Board, cancellationToken)) throw new NotFoundException(request.Board, nameof(Board));
        var people = await context.BoardPeople.AsNoTracking().Where(p => p.BoardId == request.Board).Select(p => p.UserId).ToListAsync(cancellationToken);
        return people.Order(StringComparer.Ordinal).ToList();
    }
}
