using Application.Common.Security;

namespace Application.Accounts.Commands.DeleteInvite;

/// <summary>Revokes an open invite link before it expires or runs out of uses.</summary>
[Authorize(Roles = $"{nameof(Role.Admin)},{nameof(Role.Owner)}")]
public record DeleteInviteCommand(string Id) : IRequest;

public class DeleteInviteCommandHandler(GrantService grants) : IRequestHandler<DeleteInviteCommand>
{
    public async Task Handle(DeleteInviteCommand request, CancellationToken cancellationToken)
    {
        if (!await grants.Delete(request.Id, cancellationToken))
            throw new NotFoundException(request.Id, nameof(Grant));
    }
}
