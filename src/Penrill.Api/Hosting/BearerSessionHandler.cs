using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Penrill.Core.Accounts;
using Penrill.Core.Telemetry;

namespace Penrill.Api.Hosting;

public sealed class BearerSessionHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory loggers, UrlEncoder encoder, AccountService accounts)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, loggers, encoder)
{
    public const string SchemeName = "PenrillSession";
    public const string TokenHashClaim = "penrill:token";
    public const string CredentialClaim = "penrill:credential";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.Ordinal)) return AuthenticateResult.NoResult();
        var session = await accounts.Authenticate(header["Bearer ".Length..].Trim(), Context.RequestAborted);
        if (session is null)
        {
            PenrillTelemetry.AuthFailures.Add(1, PenrillTelemetry.Tag("kind", "token"));
            return AuthenticateResult.Fail("The session is unknown or expired.");
        }
        return AuthenticateResult.Success(new AuthenticationTicket(Principal(session, SchemeName), SchemeName));
    }

    public static ClaimsPrincipal Principal(AccountSession session, string scheme) => new(new ClaimsIdentity(
    [
        new Claim(ClaimTypes.Name, session.User),
        new Claim(ClaimTypes.Role, session.Role.ToString()),
        new Claim(TokenHashClaim, session.TokenHash),
        new Claim(CredentialClaim, session.CredentialId)
    ], scheme));
}

public static class PrincipalExtensions
{
    public static string UserName(this ClaimsPrincipal user) => user.Identity?.Name ?? throw new InvalidOperationException("Not signed in.");
}
