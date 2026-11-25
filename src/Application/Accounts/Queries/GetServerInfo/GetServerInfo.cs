using Application.Admin;
using Application.Common.Models;

namespace Application.Accounts.Queries.GetServerInfo;

/// <summary>What an editor shows before signing in: the host's name and whether anyone may register.</summary>
public record GetServerInfoQuery : IRequest<ServerInfoDto>;

public sealed record ServerInfoDto(string Name, bool OpenRegistration);

public class GetServerInfoQueryHandler(AdminService admin, AccountOptions options) : IRequestHandler<GetServerInfoQuery, ServerInfoDto>
{
    public async Task<ServerInfoDto> Handle(GetServerInfoQuery request, CancellationToken cancellationToken) =>
        new(await admin.ServerName(cancellationToken), options.OpenRegistration);
}
