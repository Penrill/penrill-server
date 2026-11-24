using Penrill.Application.Common.Interfaces;
using Penrill.Application.Common.Security;

namespace Penrill.Application.Accounts.Commands.CreateInvite;

/// <summary>Creates an invite link that lets one or more people create an account.</summary>
[Authorize(Roles = $"{nameof(Penrill.Domain.Enums.Role.Admin)},{nameof(Penrill.Domain.Enums.Role.Owner)}")]
public record CreateInviteCommand(string? Role, int? Uses, int? Days) : IRequest<CreatedInviteDto>;

public sealed record CreatedInviteDto(string Id, string Token, string Role, int UsesLeft, DateTimeOffset? Expires, string CreatedBy);

public class CreateInviteCommandValidator : AbstractValidator<CreateInviteCommand>
{
    public CreateInviteCommandValidator()
    {
        RuleFor(c => c.Role)
            .Must(role => CreateInviteCommandHandler.ParseRole(role) is not null)
            .WithMessage("Role must be member or admin.");
        RuleFor(c => c.Uses)
            .InclusiveBetween(1, 1000)
            .When(c => c.Uses.HasValue);
        RuleFor(c => c.Days)
            .InclusiveBetween(1, 90)
            .When(c => c.Days.HasValue);
    }
}

public class CreateInviteCommandHandler(GrantService grants, IUser user) : IRequestHandler<CreateInviteCommand, CreatedInviteDto>
{
    public async Task<CreatedInviteDto> Handle(CreateInviteCommand request, CancellationToken cancellationToken)
    {
        var role = ParseRole(request.Role) ?? Role.Member;
        var uses = request.Uses ?? 1;
        TimeSpan? lifetime = request.Days is { } days ? TimeSpan.FromDays(days) : null;
        var created = await grants.CreateInvite(user.Id!, role, lifetime, uses, cancellationToken);
        return new(created.Grant.Id, created.Token, created.Grant.Subject.ToLowerInvariant(), created.Grant.UsesLeft, created.Grant.Expires, created.Grant.CreatedBy);
    }

    internal static Role? ParseRole(string? role) =>
        role?.Trim().ToLowerInvariant() switch
        {
            null or "" or "member" => Role.Member,
            "admin" => Role.Admin,
            _ => null
        };
}
