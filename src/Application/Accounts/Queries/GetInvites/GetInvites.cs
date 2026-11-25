using Application.Common.Security;

namespace Application.Accounts.Queries.GetInvites;

/// <summary>Open invite links on this host, newest first.</summary>
[Authorize(Roles = $"{nameof(Role.Admin)},{nameof(Role.Owner)}")]
public record GetInvitesQuery : IRequest<IReadOnlyList<InviteDto>>;

public sealed record InviteDto(string Id, string Role, int UsesLeft, DateTimeOffset? Expires, string CreatedBy, DateTimeOffset Created);

public class GetInvitesQueryHandler(GrantService grants) : IRequestHandler<GetInvitesQuery, IReadOnlyList<InviteDto>>
{
    public async Task<IReadOnlyList<InviteDto>> Handle(GetInvitesQuery request, CancellationToken cancellationToken) =>
        (await grants.Invites(cancellationToken))
            .Select(i => new InviteDto(i.Id, i.Subject.ToLowerInvariant(), i.UsesLeft, i.Expires, i.CreatedBy, i.Created))
            .ToList();
}
