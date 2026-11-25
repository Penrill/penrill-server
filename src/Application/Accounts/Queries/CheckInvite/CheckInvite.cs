namespace Application.Accounts.Queries.CheckInvite;

/// <summary>Checks whether an invite link is still valid before showing the registration form.</summary>
public record CheckInviteQuery(string Token) : IRequest;

public class CheckInviteQueryHandler(GrantService grants) : IRequestHandler<CheckInviteQuery>
{
    public async Task Handle(CheckInviteQuery request, CancellationToken cancellationToken)
    {
        if (await grants.Peek(request.Token, cancellationToken) is not { Kind: GrantKind.Invite })
            throw new NotFoundException(request.Token, nameof(Grant));
    }
}
