using Penrill.Application.Common.Exceptions;
using Penrill.Application.Common.Models;

namespace Penrill.Application.Accounts.Commands.Register;

/// <summary>Creates an account and signs it in, when the host has open registration.</summary>
public record RegisterCommand(string? Name, string? Password) : IRequest<SignedIn>;

public class RegisterCommandValidator : AbstractValidator<RegisterCommand>
{
    public RegisterCommandValidator()
    {
        RuleFor(c => c.Name).Must(name => AccountRules.NormalizeUserName(name) is not null).WithMessage("3 to 32 letters, digits, _ or -.");
        RuleFor(c => c.Password).Must(AccountRules.IsPassword).WithMessage($"{AccountRules.MinPassword} to {AccountRules.MaxPassword} characters.");
    }
}

public class RegisterCommandHandler(AccountService accounts, AccountOptions options) : IRequestHandler<RegisterCommand, SignedIn>
{
    public async Task<SignedIn> Handle(RegisterCommand request, CancellationToken cancellationToken)
    {
        if (!options.OpenRegistration) throw new ForbiddenAccessException("Registration is closed.");
        var (result, user) = await accounts.CreateAccount(request.Name, request.Password, Role.Member);
        if (user is null) throw result.Errors.Contains(AccountErrors.Taken) ? new ConflictException() : new BadRequestException();
        return await accounts.StartSession(user, cancellationToken);
    }
}
