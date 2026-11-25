using System.Security.Cryptography;
using Application.Boards;
using Application.Common.Interfaces;
using Application.Common.Security;
using Application.Sharing.Queries.GetDrawLink;

namespace Application.Sharing.Commands.EnableDrawLink;

/// <summary>Turns the board's draw link on; while it is on, asking again returns the same token.</summary>
[Authorize]
public record EnableDrawLinkCommand(string Board) : IRequest<DrawLinkDto>;

public class EnableDrawLinkCommandValidator : AbstractValidator<EnableDrawLinkCommand>
{
    public EnableDrawLinkCommandValidator() => RuleFor(c => c.Board).MustBeBoardId();
}

public class EnableDrawLinkCommandHandler(IApplicationDbContext context, IUser user) : IRequestHandler<EnableDrawLinkCommand, DrawLinkDto>
{
    public async Task<DrawLinkDto> Handle(EnableDrawLinkCommand request, CancellationToken cancellationToken)
    {
        if (!await context.OwnsAsync(user.Id!, request.Board, cancellationToken)) throw new NotFoundException(request.Board, nameof(Board));
        if (await context.BoardLinks.Where(l => l.BoardId == request.Board).Select(l => l.Token).FirstOrDefaultAsync(cancellationToken) is { } token)
            return new DrawLinkDto(token);
        var link = new BoardLink { BoardId = request.Board, Token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)) };
        context.BoardLinks.Add(link);
        await context.SaveChangesAsync(cancellationToken);
        return new DrawLinkDto(link.Token);
    }
}
