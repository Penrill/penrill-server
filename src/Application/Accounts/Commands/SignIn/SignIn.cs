namespace Application.Accounts.Commands.SignIn;

/// <summary>Signs in with a name and password and returns a session token that lasts 30 days.</summary>
public record SignInCommand(string? Name, string? Password) : IRequest<SignedIn>;

public class SignInCommandHandler(AccountService accounts) : IRequestHandler<SignInCommand, SignedIn>
{
    public async Task<SignedIn> Handle(SignInCommand request, CancellationToken cancellationToken)
    {
        var user = await accounts.CheckPassword(request.Name, request.Password) ?? throw new UnauthorizedAccessException();
        return await accounts.StartSession(user, cancellationToken);
    }
}
