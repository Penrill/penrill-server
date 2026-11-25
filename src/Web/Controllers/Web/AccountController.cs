using System.Security.Claims;
using Application.Accounts;
using Application.Admin;
using Application.Linking;
using Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Web.Hosting;

namespace Web.Controllers.Web;

[ApiController]
[Authorize(Policy = WebSessionHandler.SchemeName)]
[ServiceFilter(typeof(WebOriginFilter))]
[Route("web")]
public class AccountController : ControllerBase
{
    public sealed record ChangePasswordBody(string? Current, string? Next);

    public sealed record DecideBody(bool Allow);

    private static bool IsAdmin(ClaimsPrincipal user) => user.IsInRole(nameof(Role.Admin)) || user.IsInRole(nameof(Role.Owner));

    private static object Device(DeviceDto device) =>
        new { id = device.Id, name = device.Name, user = device.User, created = device.Created, lastUsed = device.LastUsed };

    [HttpGet("home")]
    public async Task<IActionResult> Home(
        [FromServices] ServerAddress address,
        [FromServices] ConnectLinks links,
        [FromServices] AccountService accounts,
        [FromServices] AdminService admin,
        CancellationToken cancel)
    {
        var origin = address.Origin(Request);
        var pin = address.PinFor(Request);
        var link = links.For(origin, pin);
        var devices = (await accounts.Devices(User.UserName(), cancel)).Count;
        object? counts = null;
        if (IsAdmin(User))
        {
            var all = await admin.Counts(cancel);
            counts = new { users = all.Users, boards = all.Boards, devices = all.Devices, templates = (await admin.Templates(cancel)).Count };
        }
        return Ok(new { origin, link, qr = Qr.Svg(link), shortPin = address.ShortPinFor(Request), devices, counts });
    }

    [HttpGet("devices")]
    public async Task<IActionResult> Devices([FromServices] AccountService accounts, CancellationToken cancel) =>
        Ok((await accounts.Devices(User.UserName(), cancel)).Select(Device));

    [HttpDelete("devices/{id}")]
    public async Task<IActionResult> RevokeDevice(string id, [FromServices] AccountService accounts, CancellationToken cancel) =>
        await accounts.RevokeDevice(User.UserName(), id, cancel) ? NoContent() : NotFound();

    [HttpPost("password")]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordBody body, [FromServices] AccountService accounts) =>
        await accounts.ChangePassword(User.UserName(), body.Current, body.Next) ? NoContent() : BadRequest(new { error = "password" });

    [HttpGet("link/{code}")]
    public IActionResult DescribeLink(string code, [FromServices] LinkService links) =>
        links.Describe(code) is { State: LinkState.Pending } info
            ? Ok(new { code = LinkService.Format(info.UserCode), device = info.Device, app = info.App, address = info.Address, created = info.Created })
            : NotFound();

    [HttpPost("link/{code}")]
    public IActionResult DecideLink(string code, [FromBody] DecideBody body, [FromServices] LinkService links) =>
        links.Decide(code, User.UserName(), body.Allow) ? NoContent() : NotFound();
}
