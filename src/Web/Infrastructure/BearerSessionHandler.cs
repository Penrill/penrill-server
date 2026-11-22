using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Penrill.Application.Accounts;
using Penrill.Application.Common.Telemetry;

namespace Penrill.Web.Infrastructure;

/// <summary>Signs a request in by its <c>Authorization: Bearer &lt;token&gt;</c> header (a session or a device token).</summary>
public sealed class BearerSessionHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory loggers, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, loggers, encoder)
{
    public const string SchemeName = "PenrillSession";
    public const string CredentialClaim = "penrill:credential";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.Ordinal)) return AuthenticateResult.NoResult();
        var accounts = Context.RequestServices.GetRequiredService<AccountService>();
        var session = await accounts.Authenticate(header["Bearer ".Length..].Trim(), Context.RequestAborted);
        if (session is null)
        {
            PenrillTelemetry.AuthFailures.Add(1, PenrillTelemetry.Tag("kind", "token"));
            return AuthenticateResult.Fail("The session is unknown or expired.");
        }
        return AuthenticateResult.Success(new AuthenticationTicket(Principal(session, SchemeName), SchemeName));
    }

    public static ClaimsPrincipal Principal(AuthenticatedSession session, string scheme) => new(new ClaimsIdentity(
    [
        new Claim(ClaimTypes.NameIdentifier, session.User),
        new Claim(ClaimTypes.Name, session.User),
        new Claim(ClaimTypes.Role, session.Role.ToString()),
        new Claim(CredentialClaim, session.CredentialId)
    ], scheme));
}

public static class PrincipalExtensions
{
    /// <summary>The signed-in user's name; throws for an anonymous principal.</summary>
    public static string UserName(this ClaimsPrincipal user) => user.Identity?.Name ?? throw new InvalidOperationException("Not signed in.");
}
