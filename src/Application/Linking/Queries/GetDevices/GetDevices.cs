using Application.Accounts;
using Application.Common.Interfaces;
using Application.Common.Security;

namespace Application.Linking.Queries.GetDevices;

/// <summary>The editors linked to the signed-in account; <c>Current</c> marks the one asking.</summary>
[Authorize]
public record GetDevicesQuery : IRequest<IReadOnlyList<DeviceItemDto>>;

public sealed record DeviceItemDto(string Id, string Name, DateTimeOffset Created, DateTimeOffset? LastUsed, bool Current);

public class GetDevicesQueryHandler(AccountService accounts, IUser user) : IRequestHandler<GetDevicesQuery, IReadOnlyList<DeviceItemDto>>
{
    public async Task<IReadOnlyList<DeviceItemDto>> Handle(GetDevicesQuery request, CancellationToken cancellationToken) =>
        (await accounts.Devices(user.Id!, cancellationToken))
            .Select(d => new DeviceItemDto(d.Id, d.Name, d.Created, d.LastUsed, d.Id == user.CredentialId))
            .ToList();
}
