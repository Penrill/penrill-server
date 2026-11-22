using Penrill.Application.Accounts;
using Penrill.Application.Common.Interfaces;
using Penrill.Application.Common.Security;

namespace Penrill.Application.Linking.Commands.RevokeDevice;

/// <summary>Unlinks one of the signed-in account's devices; it is signed out at once.</summary>
[Authorize]
public record RevokeDeviceCommand(string Id) : IRequest;

public class RevokeDeviceCommandHandler(AccountService accounts, IUser user) : IRequestHandler<RevokeDeviceCommand>
{
    public async Task Handle(RevokeDeviceCommand request, CancellationToken cancellationToken)
    {
        if (!await accounts.RevokeDevice(user.Id!, request.Id, cancellationToken)) throw new NotFoundException(request.Id, "device");
    }
}
