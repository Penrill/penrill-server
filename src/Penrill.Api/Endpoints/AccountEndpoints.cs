using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Penrill.Api.Hosting;
using Penrill.Core.Admin;
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
        api.MapPost("/logout", async (ClaimsPrincipal user, AccountService accounts, CancellationToken cancel) =>
        {
            await accounts.SignOut(user.FindFirstValue(BearerSessionHandler.TokenHashClaim)!, cancel);
            return Results.NoContent();
        });
        api.MapGet("/server", async (HttpContext context, AccountService accounts, AdminService admin, IPublicAddress address, CancellationToken cancel) => Results.Ok(new
        {
            service = "penrill",
            name = await admin.ServerName(cancel),
            registration = accounts.OpenRegistration ? "open" : "invite",
            link = true,
            pin = address.PinFor(context.Request)
        })).AllowAnonymous();
    }

    private static IResult ToResult(SignInResult result) => result.Status switch
    {
        SignInStatus.Ok => Results.Ok(new { name = result.Name, token = result.Token }),
        SignInStatus.Taken => Results.Conflict(),
        SignInStatus.Denied => Results.Unauthorized(),
        SignInStatus.Closed => Results.StatusCode(StatusCodes.Status403Forbidden),
        _ => Results.BadRequest()
    };
}
