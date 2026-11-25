using Application.Common.Interfaces;
using Application.Common.Security;

namespace Application.Accounts.Commands.SignOut;

/// <summary>Ends the credential the request was signed with; its live connections close.</summary>
[Authorize]
public record SignOutCommand : IRequest;

public class SignOutCommandHandler(AccountService accounts, IUser user) : IRequestHandler<SignOutCommand>
{
    public async Task Handle(SignOutCommand request, CancellationToken cancellationToken)
    {
        if (user.CredentialId is { } credential) await accounts.SignOut(credential, cancellationToken);
    }
}
