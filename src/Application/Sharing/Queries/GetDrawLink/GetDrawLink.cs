using Penrill.Application.Boards;
using Penrill.Application.Common.Interfaces;
using Penrill.Application.Common.Security;

namespace Penrill.Application.Sharing.Queries.GetDrawLink;

/// <summary>The board's draw link token, null while the link is off. Not found for anyone but the owner.</summary>
[Authorize]
public record GetDrawLinkQuery(string Board) : IRequest<DrawLinkDto>;

public sealed record DrawLinkDto(string? Token);

public class GetDrawLinkQueryValidator : AbstractValidator<GetDrawLinkQuery>
{
    public GetDrawLinkQueryValidator() => RuleFor(q => q.Board).MustBeBoardId();
}

public class GetDrawLinkQueryHandler(IApplicationDbContext context, IUser user) : IRequestHandler<GetDrawLinkQuery, DrawLinkDto>
{
    public async Task<DrawLinkDto> Handle(GetDrawLinkQuery request, CancellationToken cancellationToken)
    {
        if (!await context.OwnsAsync(user.Id!, request.Board, cancellationToken)) throw new NotFoundException(request.Board, nameof(Board));
        return new DrawLinkDto(await context.BoardLinks.Where(l => l.BoardId == request.Board).Select(l => l.Token).FirstOrDefaultAsync(cancellationToken));
    }
}
