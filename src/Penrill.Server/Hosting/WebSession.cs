using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Penrill.Api.Hosting;
using Penrill.Core.Accounts;

namespace Penrill.Server.Hosting;

/// <summary>
/// Signs people in to the server's own pages with a cookie holding a web session token. Editors never
/// use it: they send a device token as a bearer header, and the API ignores this cookie, so a page
/// on another site cannot ride on it. Pages are additionally protected by antiforgery tokens.
/// </summary>
public sealed class WebSessionHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory loggers, UrlEncoder encoder, AccountService accounts)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, loggers, encoder)
{
    public const string SchemeName = "PenrillWeb";
    public const string AdminPolicy = "PenrillAdmin";
    private const string Cookie = "penrill_web";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (Request.Cookies[Cookie] is not { Length: 64 } token) return AuthenticateResult.NoResult();
        var session = await accounts.Authenticate(token, Context.RequestAborted);
        if (session is not { Kind: CredentialKind.Session }) return AuthenticateResult.Fail("The web session is unknown or expired.");
        return AuthenticateResult.Success(new AuthenticationTicket(BearerSessionHandler.Principal(session, SchemeName), SchemeName));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.Redirect("/login?return=" + Uri.EscapeDataString(Request.Path + Request.QueryString));
        return Task.CompletedTask;
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        Response.Redirect("/");
        return Task.CompletedTask;
    }

    public static void SignIn(HttpContext context, string token) =>
        context.Response.Cookies.Append(Cookie, token, new CookieOptions
        {
            HttpOnly = true,
            Secure = context.Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            Path = "/",
            MaxAge = AccountService.SessionLifetime
        });

    public static async Task SignOut(HttpContext context, AccountService accounts)
    {
        if (context.Request.Cookies[Cookie] is { Length: 64 } token)
            await accounts.SignOut(AccountService.HashToken(token), context.RequestAborted);
        context.Response.Cookies.Delete(Cookie, new CookieOptions { Path = "/" });
    }

    /// <summary>Only local paths, so a crafted sign-in link cannot send people to another site.</summary>
    public static string SafeReturn(string? target) =>
        target is { Length: > 0 } && target[0] == '/' && !target.StartsWith("//", StringComparison.Ordinal) && !target.StartsWith("/\\", StringComparison.Ordinal)
            ? target
            : "/";
}
