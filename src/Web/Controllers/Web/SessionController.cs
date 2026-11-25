using System.Net;
using System.Security.Claims;
using Application.Accounts;
using Application.Admin;
using Domain.Constants;
using Domain.Enums;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using Web.Hosting;

namespace Web.Controllers.Web;

[ApiController]
[ServiceFilter(typeof(WebOriginFilter))]
[Route("web")]
public class SessionController : ControllerBase
{
    public sealed record SignInBody(string? Name, string? Password);

    public sealed record SetupBody(string? Token, string? Name, string? Password, string? ServerName, string? Email);

    public sealed record PasswordBody(string? Password);

    private IActionResult Error(string code, int status = StatusCodes.Status400BadRequest) =>
        StatusCode(status, new { error = code });

    private static string Code(RedeemStatus status) => status switch
    {
        RedeemStatus.Taken => "taken",
        RedeemStatus.Expired => "expired",
        _ => "invalid"
    };

    private static bool IsAdmin(ClaimsPrincipal user) => user.IsInRole(nameof(Role.Admin)) || user.IsInRole(nameof(Role.Owner));

    [AllowAnonymous]
    [HttpGet("session")]
    public async Task<IActionResult> GetSession([FromServices] AdminService admin, [FromServices] GrantService grants, CancellationToken cancel)
    {
        var user = (await HttpContext.AuthenticateAsync(WebSessionHandler.SchemeName)).Principal ?? new ClaimsPrincipal();
        return Ok(new
        {
            server = await admin.ServerName(cancel),
            unclaimed = await grants.Unclaimed(),
            user = user.Identity?.IsAuthenticated == true
                ? new { name = user.UserName(), role = IsAdmin(user) ? user.IsInRole(nameof(Role.Owner)) ? "owner" : "admin" : "member" }
                : null
        });
    }

    [AllowAnonymous]
    [EnableRateLimiting(RateLimits.Account)]
    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] SignInBody body, [FromServices] AccountService accounts) =>
        await StartSession(accounts, body.Name, body.Password) ? NoContent() : Error("wrong", StatusCodes.Status401Unauthorized);

    [AllowAnonymous]
    [HttpPost("logout")]
    public async Task<IActionResult> Logout([FromServices] AccountService accounts)
    {
        await WebSessionHandler.SignOut(HttpContext, accounts);
        return NoContent();
    }

    [AllowAnonymous]
    [HttpGet("setup")]
    public async Task<IActionResult> GetSetup([FromServices] GrantService grants, [FromServices] IWebHostEnvironment environment, CancellationToken cancel)
    {
        if (!await grants.Unclaimed()) return NotFound();
        var token = environment.IsDevelopment() && HttpContext.Connection.RemoteIpAddress is { } ip && IPAddress.IsLoopback(ip)
            ? await grants.CreateClaim(cancel)
            : null;
        return Ok(new { token });
    }

    [AllowAnonymous]
    [EnableRateLimiting(RateLimits.Account)]
    [HttpPost("setup")]
    public async Task<IActionResult> Setup(
        [FromBody] SetupBody body,
        [FromServices] GrantService grants,
        [FromServices] AccountService accounts,
        [FromServices] AdminService admin,
        CancellationToken cancel)
    {
        if (AccountRules.CleanEmail(body.Email) is null) return Error("email");
        if (body.ServerName?.Trim() is not { Length: > 0 and <= AccountRules.MaxServerName }) return Error("serverName");
        var result = await grants.Claim(body.Token, body.Name, body.Password, cancel);
        if (result.Status != RedeemStatus.Ok) return Error(Code(result.Status));
        await admin.Rename(body.ServerName, cancel);
        await admin.SetContactEmail(body.Email, cancel);
        await StartSession(accounts, body.Name, body.Password);
        return NoContent();
    }

    [AllowAnonymous]
    [HttpGet("invite/{token}")]
    public async Task<IActionResult> CheckInvite(string token, [FromServices] GrantService grants, CancellationToken cancel) =>
        await grants.Peek(token, cancel) is { Kind: GrantKind.Invite } ? NoContent() : NotFound();

    [AllowAnonymous]
    [EnableRateLimiting(RateLimits.Account)]
    [HttpPost("invite/{token}")]
    public async Task<IActionResult> AcceptInvite(string token, [FromBody] SignInBody body, [FromServices] GrantService grants, [FromServices] AccountService accounts, CancellationToken cancel)
    {
        var result = await grants.AcceptInvite(token, body.Name, body.Password, cancel);
        if (result.Status != RedeemStatus.Ok) return Error(Code(result.Status));
        await StartSession(accounts, body.Name, body.Password);
        return NoContent();
    }

    [AllowAnonymous]
    [HttpGet("reset/{token}")]
    public async Task<IActionResult> CheckReset(string token, [FromServices] GrantService grants, CancellationToken cancel) =>
        await grants.Peek(token, cancel) is { Kind: GrantKind.Reset } grant ? Ok(new { user = grant.Subject }) : NotFound();

    [AllowAnonymous]
    [EnableRateLimiting(RateLimits.Account)]
    [HttpPost("reset/{token}")]
    public async Task<IActionResult> ResetPassword(string token, [FromBody] PasswordBody body, [FromServices] GrantService grants, CancellationToken cancel)
    {
        var result = await grants.Reset(token, body.Password, cancel);
        return result.Status == RedeemStatus.Ok ? NoContent() : Error(Code(result.Status));
    }

    private async Task<bool> StartSession(AccountService accounts, string? name, string? password)
    {
        if (await accounts.CheckPassword(name, password) is not { } user) return false;
        var session = await accounts.StartSession(user, HttpContext.RequestAborted);
        WebSessionHandler.SignIn(HttpContext, session.Token);
        return true;
    }
}
