using System.Security.Cryptography;
using Application.Accounts;
using Application.Common.Exceptions;
using Application.Common.Interfaces;

namespace Application.Sharing.Commands.JoinBoard;

/// <summary>
/// Opens a board by its draw link. Someone without an account here gets a guest account that works on that board only,
/// named after the label they typed. A device that already has an account (from an earlier link, or a real one)
/// keeps it and just gets the board; <c>Token</c> is then null.
/// </summary>
public record JoinBoardCommand(string? Token, string? Name) : IRequest<JoinedDto>;

public sealed record JoinedDto(string Name, string? Token, string Board);

public class JoinBoardCommandHandler(IApplicationDbContext context, IUser user, AccountService accounts) : IRequestHandler<JoinBoardCommand, JoinedDto>
{
    public async Task<JoinedDto> Handle(JoinBoardCommand request, CancellationToken cancellationToken)
    {
        var token = request.Token;
        if (string.IsNullOrWhiteSpace(token) || token.Length > 64 ||
            await context.BoardLinks.Where(l => l.Token == token).Select(l => l.BoardId).FirstOrDefaultAsync(cancellationToken) is not { } board)
            throw new NotFoundException("draw link", nameof(BoardLink));
        if (user.Id is { } signedIn)
        {
            await AddGuest(board, token, signedIn, cancellationToken);
            return new JoinedDto(signedIn, null, board);
        }
        if (string.IsNullOrWhiteSpace(request.Name)) throw new BadRequestException();
        var label = new string(request.Name.ToLowerInvariant().Where(char.IsAsciiLetterOrDigit).Take(20).ToArray());
        if (label.Length < 2) label = "guest";
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var name = $"{label}-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(2))}";
            var (_, created) = await accounts.CreateAccount(name, AccountService.NewToken(), Role.Guest);
            if (created is null) continue;
            await AddGuest(board, token, created, cancellationToken);
            return new JoinedDto(created, await accounts.IssueDeviceToken(created, "Guest", cancellationToken), board);
        }
        throw new ConflictException();
    }

    private async Task AddGuest(string board, string token, string user, CancellationToken cancellationToken)
    {
        if (await context.BoardPeople.AnyAsync(p => p.BoardId == board && p.UserId == user, cancellationToken)) return;
        if (await context.Boards.AnyAsync(b => b.Id == board && b.Owner == user, cancellationToken)) return;
        context.BoardPeople.Add(new BoardPerson { BoardId = board, UserId = user, Via = token });
        await context.SaveChangesAsync(cancellationToken);
    }
}
