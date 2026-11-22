using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Penrill.Web.Infrastructure;
using Penrill.Application.Accounts;
using Penrill.Domain.Constants;
using Penrill.Domain.Enums;
using Penrill.Application.Admin;
using Penrill.Application.Linking;
using Penrill.Server.Hosting;

namespace Penrill.Server.Web;

/// <summary>
/// The JSON API behind the server's own web pages (the React app in <c>wwwroot</c>). It signs people
/// in with the web session cookie, never with device tokens. Errors come back as <c>{ error: "code" }</c>
/// and the page words them in the visitor's language.
///
/// Cross-site requests are refused twice: the cookie is SameSite=Lax, and every request that changes
/// something must carry the <see cref="Header"/> header, which a page on another site cannot send
/// without a CORS preflight this server never allows.
/// </summary>
public static class WebEndpoints
{
    public const string Header = "X-Penrill-Web";

    /// <summary>The same limit as signing in through the API (see PenrillApiExtensions).</summary>
    private const string RateLimit = "account";

    public sealed record SignInBody(string? Name, string? Password);

    public sealed record SetupBody(string? Token, string? Name, string? Password, string? ServerName, string? Email);

    public sealed record PasswordBody(string? Password);

    public sealed record ChangePasswordBody(string? Current, string? Next);

    public sealed record DecideBody(bool Allow);

    public sealed record InviteBody(string? Role, int Uses, int Days);

    public sealed record RoleBody(string? Role);

    public sealed record DisabledBody(bool Disabled);

    public sealed record OwnerBody(string? User);

    public sealed record NameBody(string? Name);

    public sealed record EmailBody(string? Email);

    private static IResult Error(string code, int status = StatusCodes.Status400BadRequest) => Results.Json(new { error = code }, statusCode: status);

    private static string Code(RedeemStatus status) => status switch
    {
        RedeemStatus.Taken => "taken",
        RedeemStatus.Expired => "expired",
        _ => "invalid"
    };

    /// <summary>Roles that can be given in the web app. The owner stays the owner.</summary>
    private static Role? Grantable(string? role) =>
        Enum.TryParse<Role>(role, ignoreCase: true, out var parsed) && parsed is Role.Member or Role.Admin ? parsed : null;

    private static bool IsAdmin(ClaimsPrincipal user) => user.IsInRole(nameof(Role.Admin)) || user.IsInRole(nameof(Role.Owner));

    private static string Version => typeof(WebEndpoints).Assembly.GetName().Version?.ToString(3) ?? "";

    public static void Map(WebApplication app)
    {
        var web = app.MapGroup("/web").RequireAuthorization(WebSessionHandler.SchemeName).AddEndpointFilter(async (context, next) =>
        {
            var request = context.HttpContext.Request;
            if (request.Headers["Sec-Fetch-Site"] is [{ } site] && site is not ("same-origin" or "none"))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (!HttpMethods.IsGet(request.Method) && !request.Headers.ContainsKey(Header))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            return await next(context);
        });

        MapSession(web);
        MapAccount(web);
        MapAdmin(web.MapGroup("/admin").RequireAuthorization(WebSessionHandler.AdminPolicy));
    }

    /// <summary>Who is here, signing in and out, and the pages people reach by a link: setup, invites, password resets.</summary>
    private static void MapSession(RouteGroupBuilder web)
    {
        // Anonymous, so the cookie is not read by the authorization step: look at it here.
        web.MapGet("/session", async (HttpContext context, AdminService admin, GrantService grants, CancellationToken cancel) =>
        {
            var user = (await context.AuthenticateAsync(WebSessionHandler.SchemeName)).Principal ?? new ClaimsPrincipal();
            return Results.Ok(new
        {
            server = await admin.ServerName(cancel),
            unclaimed = await grants.Unclaimed(),
            user = user.Identity?.IsAuthenticated == true
                ? new { name = user.UserName(), role = IsAdmin(user) ? user.IsInRole(nameof(Role.Owner)) ? "owner" : "admin" : "member" }
                : null
        });
        }).AllowAnonymous();

        web.MapPost("/login", async (SignInBody body, HttpContext context, AccountService accounts) =>
            await StartSession(context, accounts, body.Name, body.Password) ? Results.NoContent() : Error("wrong", StatusCodes.Status401Unauthorized))
            .AllowAnonymous().RequireRateLimiting(RateLimit);

        web.MapPost("/logout", async (HttpContext context, AccountService accounts) =>
        {
            await WebSessionHandler.SignOut(context, accounts);
            return Results.NoContent();
        }).AllowAnonymous();

        web.MapGet("/setup", async (HttpContext context, GrantService grants, IWebHostEnvironment environment, CancellationToken cancel) =>
        {
            if (!await grants.Unclaimed()) return Results.NotFound();
            // Development only (dotnet run, Aspire): the browser on this machine gets the claim directly,
            // so nobody has to dig the link out of the console. Installed servers never do this.
            var token = environment.IsDevelopment() && context.Connection.RemoteIpAddress is { } ip && IPAddress.IsLoopback(ip)
                ? await grants.CreateClaim(cancel)
                : null;
            return Results.Ok(new { token });
        }).AllowAnonymous();

        web.MapPost("/setup", async (SetupBody body, HttpContext context, GrantService grants, AccountService accounts, AdminService admin, CancellationToken cancel) =>
        {
            if (AccountRules.CleanEmail(body.Email) is null) return Error("email");
            if (body.ServerName?.Trim() is not { Length: > 0 and <= AccountRules.MaxServerName }) return Error("serverName");
            var result = await grants.Claim(body.Token, body.Name, body.Password, cancel);
            if (result.Status != RedeemStatus.Ok) return Error(Code(result.Status));
            await admin.Rename(body.ServerName, cancel);
            await admin.SetContactEmail(body.Email, cancel);
            await StartSession(context, accounts, body.Name, body.Password);
            return Results.NoContent();
        }).AllowAnonymous().RequireRateLimiting(RateLimit);

        web.MapGet("/invite/{token}", async (string token, GrantService grants, CancellationToken cancel) =>
            await grants.Peek(token, cancel) is { Kind: GrantKind.Invite } ? Results.NoContent() : Results.NotFound()).AllowAnonymous();

        web.MapPost("/invite/{token}", async (string token, SignInBody body, HttpContext context, GrantService grants, AccountService accounts, CancellationToken cancel) =>
        {
            var result = await grants.AcceptInvite(token, body.Name, body.Password, cancel);
            if (result.Status != RedeemStatus.Ok) return Error(Code(result.Status));
            await StartSession(context, accounts, body.Name, body.Password);
            return Results.NoContent();
        }).AllowAnonymous().RequireRateLimiting(RateLimit);

        web.MapGet("/reset/{token}", async (string token, GrantService grants, CancellationToken cancel) =>
            await grants.Peek(token, cancel) is { Kind: GrantKind.Reset } grant ? Results.Ok(new { user = grant.Subject }) : Results.NotFound()).AllowAnonymous();

        web.MapPost("/reset/{token}", async (string token, PasswordBody body, GrantService grants, CancellationToken cancel) =>
        {
            var result = await grants.Reset(token, body.Password, cancel);
            return result.Status == RedeemStatus.Ok ? Results.NoContent() : Error(Code(result.Status));
        }).AllowAnonymous().RequireRateLimiting(RateLimit);
    }

    /// <summary>The signed-in person's own things: the home page, their devices and password, and allowing a new device.</summary>
    private static void MapAccount(RouteGroupBuilder web)
    {
        web.MapGet("/home", async (HttpContext context, ClaimsPrincipal user, ServerAddress address, ConnectLinks links, AccountService accounts, AdminService admin, CancellationToken cancel) =>
        {
            var origin = address.Origin(context.Request);
            var pin = address.PinFor(context.Request);
            var link = links.For(origin, pin);
            var devices = (await accounts.Devices(user.UserName(), cancel)).Count;
            object? counts = null;
            if (IsAdmin(user))
            {
                var all = await admin.Counts(cancel);
                counts = new { users = all.Users, boards = all.Boards, devices = all.Devices, templates = (await admin.Templates(cancel)).Count };
            }
            return Results.Ok(new { origin, link, qr = Qr.Svg(link), shortPin = address.ShortPinFor(context.Request), devices, counts });
        });

        web.MapGet("/devices", async (ClaimsPrincipal user, AccountService accounts, CancellationToken cancel) =>
            Results.Ok((await accounts.Devices(user.UserName(), cancel)).Select(Device)));

        web.MapDelete("/devices/{id}", async (string id, ClaimsPrincipal user, AccountService accounts, CancellationToken cancel) =>
            await accounts.RevokeDevice(user.UserName(), id, cancel) ? Results.NoContent() : Results.NotFound());

        web.MapPost("/password", async (ChangePasswordBody body, ClaimsPrincipal user, AccountService accounts, CancellationToken cancel) =>
            await accounts.ChangePassword(user.UserName(), body.Current, body.Next) ? Results.NoContent() : Error("password"));

        web.MapGet("/link/{code}", (string code, LinkService links) =>
            links.Describe(code) is { State: LinkState.Pending } info
                ? Results.Ok(new { code = LinkService.Format(info.UserCode), device = info.Device, app = info.App, address = info.Address, created = info.Created })
                : Results.NotFound());

        web.MapPost("/link/{code}", (string code, DecideBody body, ClaimsPrincipal user, LinkService links) =>
            links.Decide(code, user.UserName(), body.Allow) ? Results.NoContent() : Results.NotFound());
    }

    private static void MapAdmin(RouteGroupBuilder admin)
    {
        admin.MapGet("/people", async (AdminService service, CancellationToken cancel) => Results.Ok(new
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
        }));

        admin.MapPost("/invites", async (InviteBody body, HttpContext context, ClaimsPrincipal user, AdminService service, ServerAddress address, CancellationToken cancel) =>
        {
            if (Grantable(body.Role) is not { } role) return Error("role");
            var invite = await service.Invite(user.UserName(), role, TimeSpan.FromDays(Math.Clamp(body.Days, 1, 90)), Math.Clamp(body.Uses, 1, 1000), cancel);
            var url = $"{address.Origin(context.Request)}/invite/{invite.Token}";
            return Results.Ok(new { url, qr = Qr.Svg(url) });
        });

        admin.MapDelete("/invites/{id}", async (string id, AdminService service, CancellationToken cancel) =>
            await service.DeleteInvite(id, cancel) ? Results.NoContent() : Results.NotFound());

        admin.MapPut("/people/{name}/role", async (string name, RoleBody body, AdminService service, CancellationToken cancel) =>
            Grantable(body.Role) is { } role && await service.SetRole(name, role, cancel) ? Results.NoContent() : Error("owner"));

        admin.MapPut("/people/{name}/disabled", async (string name, DisabledBody body, AdminService service, CancellationToken cancel) =>
            await service.SetDisabled(name, body.Disabled, cancel) ? Results.NoContent() : Error("owner"));

        admin.MapPost("/people/{name}/reset", async (string name, HttpContext context, ClaimsPrincipal user, AdminService service, ServerAddress address, CancellationToken cancel) =>
        {
            if (await service.ResetLink(user.UserName(), name, cancel) is not { } reset) return Results.NotFound();
            var url = $"{address.Origin(context.Request)}/reset/{reset.Token}";
            return Results.Ok(new { url, qr = Qr.Svg(url) });
        });

        admin.MapGet("/devices", async (AdminService service, CancellationToken cancel) =>
            Results.Ok((await service.Devices(cancel)).Select(Device)));

        admin.MapDelete("/devices/{id}", async (string id, AdminService service, CancellationToken cancel) =>
            await service.RevokeDevice(id, cancel) ? Results.NoContent() : Results.NotFound());

        admin.MapGet("/templates", async (AdminService service, CancellationToken cancel) =>
            Results.Ok((await service.Templates(cancel)).Select(t => new { id = t.Id, name = t.Name, author = t.Author, folder = t.FolderName, updated = t.Updated })));

        admin.MapDelete("/templates/{id}", async (string id, ClaimsPrincipal user, AdminService service, CancellationToken cancel) =>
            await service.RemoveTemplate(id, cancel) ? Results.NoContent() : Results.NotFound());

        admin.MapGet("/boards", async (AdminService service, CancellationToken cancel) =>
            Results.Ok((await service.Orphans(cancel)).Select(b => new { id = b.Id, title = b.Title, updated = b.Updated })));

        admin.MapPut("/boards/{id}/owner", async (string id, OwnerBody body, AdminService service, CancellationToken cancel) =>
            await service.AssignOwner(id, body.User, cancel) ? Results.NoContent() : Results.NotFound());

        admin.MapGet("/server", async (HttpContext context, AdminService service, ServerAddress address, CancellationToken cancel) =>
        {
            var counts = await service.Counts(cancel);
            return Results.Ok(new
            {
                name = await service.ServerName(cancel),
                email = await service.ContactEmail(cancel),
                origin = address.Origin(context.Request),
                lan = address.Mode == AddressMode.Lan,
                shortPin = address.ShortPinFor(context.Request),
                version = Version,
                users = counts.Users,
                devices = counts.Devices,
                boards = counts.Boards,
                orphans = (await service.Orphans(cancel)).Count
            });
        });

        admin.MapPut("/server/name", async (NameBody body, AdminService service, CancellationToken cancel) =>
            await service.Rename(body.Name, cancel) ? Results.NoContent() : Error("serverName"));

        admin.MapPut("/server/email", async (EmailBody body, AdminService service, CancellationToken cancel) =>
            await service.SetContactEmail(body.Email, cancel) ? Results.NoContent() : Error("email"));

        // A plain link downloads it, so this GET is exempt from the header; Sec-Fetch-Site still keeps other sites out.
        admin.MapGet("/backup", async (HttpContext context, AdminService service, CancellationToken cancel) =>
        {
            context.Response.ContentType = "application/zip";
            context.Response.Headers.ContentDisposition = $"attachment; filename=\"penrill-backup-{DateTime.Now:yyyy-MM-dd}.zip\"";
            var buffer = new MemoryStream();
            await service.Backup(buffer, cancel);
            buffer.Position = 0;
            await buffer.CopyToAsync(context.Response.Body, cancel);
        });
    }

    private static object Device(DeviceDto device) =>
        new { id = device.Id, name = device.Name, user = device.User, created = device.Created, lastUsed = device.LastUsed };

    /// <summary>Signs in with a fresh web session after a claim, an invite or a password sign-in.</summary>
    private static async Task<bool> StartSession(HttpContext context, AccountService accounts, string? name, string? password)
    {
        if (await accounts.CheckPassword(name, password) is not { } user) return false;
        var session = await accounts.StartSession(user, context.RequestAborted);
        WebSessionHandler.SignIn(context, session.Token);
        return true;
    }
}
