using Application.Accounts;
using Application.Admin;
using Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Web.Hosting;

namespace Web.Controllers.Web;

[ApiController]
[Authorize(Policy = WebSessionHandler.AdminPolicy)]
[ServiceFilter(typeof(WebOriginFilter))]
[Route("web/admin")]
public class AdminController : ControllerBase
{
    public sealed record InviteBody(string? Role, int Uses, int Days);

    public sealed record RoleBody(string? Role);

    public sealed record DisabledBody(bool Disabled);

    public sealed record OwnerBody(string? User);

    public sealed record NameBody(string? Name);

    public sealed record EmailBody(string? Email);

    private static Role? Grantable(string? role) =>
        Enum.TryParse<Role>(role, ignoreCase: true, out var parsed) && parsed is Role.Member or Role.Admin ? parsed : null;

    private static string Version => typeof(AdminController).Assembly.GetName().Version?.ToString(3) ?? "";

    private static object Device(DeviceDto device) =>
        new { id = device.Id, name = device.Name, user = device.User, created = device.Created, lastUsed = device.LastUsed };

    [HttpGet("people")]
    public async Task<IActionResult> People([FromServices] AdminService service, CancellationToken cancel) => Ok(new
    {
        users = (await service.Users(cancel)).Select(u => new { name = u.Name, role = u.Role.ToString().ToLowerInvariant(), disabled = u.Disabled, created = u.Created }),
        invites = (await service.Invites(cancel)).Select(i => new
        {
            id = i.Id,
            role = i.Subject.ToLowerInvariant(),
            usesLeft = i.UsesLeft,
            expires = i.Expires,
            createdBy = i.CreatedBy
        })
    });

    [HttpPost("invites")]
    public async Task<IActionResult> CreateInvite([FromBody] InviteBody body, [FromServices] AdminService service, [FromServices] ServerAddress address, CancellationToken cancel)
    {
        if (Grantable(body.Role) is not { } role) return BadRequest(new { error = "role" });
        var invite = await service.Invite(User.UserName(), role, TimeSpan.FromDays(Math.Clamp(body.Days, 1, 90)), Math.Clamp(body.Uses, 1, 1000), cancel);
        var url = $"{address.Origin(Request)}/invite/{invite.Token}";
        return Ok(new { url, qr = Qr.Svg(url) });
    }

    [HttpDelete("invites/{id}")]
    public async Task<IActionResult> DeleteInvite(string id, [FromServices] AdminService service, CancellationToken cancel) =>
        await service.DeleteInvite(id, cancel) ? NoContent() : NotFound();

    [HttpPut("people/{name}/role")]
    public async Task<IActionResult> SetRole(string name, [FromBody] RoleBody body, [FromServices] AdminService service, CancellationToken cancel) =>
        Grantable(body.Role) is { } role && await service.SetRole(name, role, cancel) ? NoContent() : BadRequest(new { error = "owner" });

    [HttpPut("people/{name}/disabled")]
    public async Task<IActionResult> SetDisabled(string name, [FromBody] DisabledBody body, [FromServices] AdminService service, CancellationToken cancel) =>
        await service.SetDisabled(name, body.Disabled, cancel) ? NoContent() : BadRequest(new { error = "owner" });

    [HttpPost("people/{name}/reset")]
    public async Task<IActionResult> ResetLink(string name, [FromServices] AdminService service, [FromServices] ServerAddress address, CancellationToken cancel)
    {
        if (await service.ResetLink(User.UserName(), name, cancel) is not { } reset) return NotFound();
        var url = $"{address.Origin(Request)}/reset/{reset.Token}";
        return Ok(new { url, qr = Qr.Svg(url) });
    }

    [HttpGet("devices")]
    public async Task<IActionResult> Devices([FromServices] AdminService service, CancellationToken cancel) =>
        Ok((await service.Devices(cancel)).Select(Device));

    [HttpDelete("devices/{id}")]
    public async Task<IActionResult> RevokeDevice(string id, [FromServices] AdminService service, CancellationToken cancel) =>
        await service.RevokeDevice(id, cancel) ? NoContent() : NotFound();

    [HttpGet("templates")]
    public async Task<IActionResult> Templates([FromServices] AdminService service, CancellationToken cancel) =>
        Ok((await service.Templates(cancel)).Select(t => new { id = t.Id, name = t.Name, author = t.Author, folder = t.FolderName, updated = t.Updated }));

    [HttpDelete("templates/{id}")]
    public async Task<IActionResult> RemoveTemplate(string id, [FromServices] AdminService service, CancellationToken cancel) =>
        await service.RemoveTemplate(id, cancel) ? NoContent() : NotFound();

    [HttpGet("boards")]
    public async Task<IActionResult> Boards([FromServices] AdminService service, CancellationToken cancel) =>
        Ok((await service.Orphans(cancel)).Select(b => new { id = b.Id, title = b.Title, updated = b.Updated }));

    [HttpPut("boards/{id}/owner")]
    public async Task<IActionResult> AssignOwner(string id, [FromBody] OwnerBody body, [FromServices] AdminService service, CancellationToken cancel) =>
        await service.AssignOwner(id, body.User, cancel) ? NoContent() : NotFound();

    [HttpGet("server")]
    public async Task<IActionResult> Server([FromServices] AdminService service, [FromServices] ServerAddress address, CancellationToken cancel)
    {
        var counts = await service.Counts(cancel);
        return Ok(new
        {
            name = await service.ServerName(cancel),
            email = await service.ContactEmail(cancel),
            origin = address.Origin(Request),
            lan = address.Mode == AddressMode.Lan,
            shortPin = address.ShortPinFor(Request),
            version = Version,
            users = counts.Users,
            devices = counts.Devices,
            boards = counts.Boards,
            orphans = (await service.Orphans(cancel)).Count
        });
    }

    [HttpPut("server/name")]
    public async Task<IActionResult> Rename([FromBody] NameBody body, [FromServices] AdminService service, CancellationToken cancel) =>
        await service.Rename(body.Name, cancel) ? NoContent() : BadRequest(new { error = "serverName" });

    [HttpPut("server/email")]
    public async Task<IActionResult> SetEmail([FromBody] EmailBody body, [FromServices] AdminService service, CancellationToken cancel) =>
        await service.SetContactEmail(body.Email, cancel) ? NoContent() : BadRequest(new { error = "email" });

    [HttpGet("backup")]
    public async Task<IActionResult> Backup([FromServices] AdminService service, CancellationToken cancel)
    {
        var buffer = new MemoryStream();
        await service.Backup(buffer, cancel);
        buffer.Position = 0;
        return File(buffer, "application/zip", $"penrill-backup-{DateTime.Now:yyyy-MM-dd}.zip");
    }
}
