using Penrill.Application.Common.Exceptions;

namespace Penrill.Application.Accounts.Commands.AcceptInvite;

/// <summary>Creates an account using an invite link and signs it in.</summary>
public record AcceptInviteCommand(string Token, string? Name, string? Password) : IRequest<SignedIn>;

public class AcceptInviteCommandValidator : AbstractValidator<AcceptInviteCommand>
{
    public AcceptInviteCommandValidator()
    {
        RuleFor(c => c.Name).Must(name => AccountRules.NormalizeUserName(name) is not null).WithMessage("3 to 32 letters, digits, _ or -.");
        RuleFor(c => c.Password).Must(AccountRules.IsPassword).WithMessage($"{AccountRules.MinPassword} to {AccountRules.MaxPassword} characters.");
    }
}

public class AcceptInviteCommandHandler(GrantService grants, AccountService accounts) : IRequestHandler<AcceptInviteCommand, SignedIn>
{
    public async Task<SignedIn> Handle(AcceptInviteCommand request, CancellationToken cancellationToken)
    {
        var result = await grants.AcceptInvite(request.Token, request.Name, request.Password, cancellationToken);
        return result.Status switch
        {
            RedeemStatus.Ok => await accounts.StartSession(result.User!, cancellationToken),
            RedeemStatus.Taken => throw new ConflictException(),
            RedeemStatus.Expired => throw new NotFoundException(request.Token, nameof(Grant)),
            _ => throw new BadRequestException()
        };
    }
}
