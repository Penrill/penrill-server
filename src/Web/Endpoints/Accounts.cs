using Microsoft.AspNetCore.Http.HttpResults;
using Penrill.Application.Accounts;
using Penrill.Application.Accounts.Commands.Register;
using Penrill.Application.Accounts.Commands.SignIn;
using Penrill.Application.Accounts.Commands.SignOut;
using Penrill.Application.Accounts.Queries.GetServerInfo;

namespace Penrill.Web.Endpoints;

/// <summary>Accounts: register, sign in and out, and what the host is.</summary>
public class Accounts : IEndpointGroup
{
    public static string? RoutePrefix => "/api";

    public sealed record Credentials(string? Name, string? Password);

    public static void Map(RouteGroupBuilder groupBuilder)
    {
        groupBuilder.RequireAuthorization();
        groupBuilder.MapPost(Register, "/register").AllowAnonymous().RequireRateLimiting(RateLimits.Account);
        groupBuilder.MapPost(Login, "/login").AllowAnonymous().RequireRateLimiting(RateLimits.Account);
        groupBuilder.MapPost(Logout, "/logout");
        groupBuilder.MapGet(GetServer, "/server").AllowAnonymous();
    }

    [EndpointSummary("Create an account and sign in")]
    [EndpointDescription("Only when the host has open registration (403 otherwise). 409 when the name is taken.")]
    public static async Task<Ok<SignedIn>> Register(ISender sender, Credentials input) =>
        TypedResults.Ok(await sender.Send(new RegisterCommand(input.Name, input.Password)));

    [EndpointSummary("Sign in with a name and password")]
    public static async Task<Ok<SignedIn>> Login(ISender sender, Credentials input) =>
        TypedResults.Ok(await sender.Send(new SignInCommand(input.Name, input.Password)));

    [EndpointSummary("Sign out this session or device")]
    public static async Task<NoContent> Logout(ISender sender)
    {
        await sender.Send(new SignOutCommand());
        return TypedResults.NoContent();
    }

    [EndpointSummary("What this host is")]
    [EndpointDescription("Editors check service == \"penrill\" before connecting. Pin is set when the certificate is not publicly trusted.")]
    public static async Task<Ok<ServerResponse>> GetServer(ISender sender, HttpContext context, IPublicAddress address)
    {
        var info = await sender.Send(new GetServerInfoQuery());
        return TypedResults.Ok(new ServerResponse("penrill", info.Name, info.OpenRegistration ? "open" : "invite", true, address.PinFor(context.Request)));
    }

    public sealed record ServerResponse(string Service, string Name, string Registration, bool Link, string? Pin);
}
