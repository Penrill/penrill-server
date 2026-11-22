using System.Security.Claims;
using Penrill.Application.Common.Interfaces;
using Penrill.Web.Infrastructure;

namespace Penrill.Web.Services;

public class CurrentUser(IHttpContextAccessor httpContextAccessor) : IUser
{
    private ClaimsPrincipal? Principal => httpContextAccessor.HttpContext?.User;

    public string? Id => Principal?.FindFirstValue(ClaimTypes.NameIdentifier);

    public List<string>? Roles => Principal?.FindAll(ClaimTypes.Role).Select(x => x.Value).ToList();

    public string? CredentialId => Principal?.FindFirstValue(BearerSessionHandler.CredentialClaim);
}
