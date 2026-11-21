using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Penrill.Api.Hosting;
using Penrill.Api.Live;
using Penrill.Core.Accounts;

namespace Penrill.Api.Endpoints;

internal static class AccountEndpoints
{
    public const string RateLimit = "account";

    public sealed record Credentials(string? Name, string? Password);

    public static void Map(RouteGroupBuilder api)
    {
        api.MapPost("/register", async (Credentials input, AccountService accounts, CancellationToken cancel) =>
            ToResult(await accounts.Register(input.Name, input.Password, cancel))).AllowAnonymous().RequireRateLimiting(RateLimit);
        api.MapPost("/login", async (Credentials input, AccountService accounts, CancellationToken cancel) =>
            ToResult(await accounts.SignIn(input.Name, input.Password, cancel))).AllowAnonymous().RequireRateLimiting(RateLimit);
        api.MapPost("/logout", async (ClaimsPrincipal user, AccountService accounts, LiveConnections live, CancellationToken cancel) =>
        {
            var tokenHash = user.FindFirstValue(BearerSessionHandler.TokenHashClaim)!;
            await accounts.SignOut(tokenHash, cancel);
            live.CloseSession(tokenHash);
            return Results.NoContent();
        });
    }

    private static IResult ToResult(SignInResult result) => result.Status switch
    {
        SignInStatus.Ok => Results.Ok(new { name = result.Name, token = result.Token }),
        SignInStatus.Taken => Results.Conflict(),
        SignInStatus.Denied => Results.Unauthorized(),
        _ => Results.BadRequest()
    };
}
