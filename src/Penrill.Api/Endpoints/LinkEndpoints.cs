using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Penrill.Api.Hosting;
using Penrill.Core.Accounts;
using Penrill.Core.Linking;

namespace Penrill.Api.Endpoints;

/// <summary>Device linking (see <see cref="LinkService"/>) and the devices an account has linked.</summary>
internal static class LinkEndpoints
{
    /// <summary>A device polls every few seconds while someone approves it, so this is looser than the sign-in limit.</summary>
    public const string PollLimit = "link-poll";

    public sealed record StartBody(string? Device, string? App);

    public sealed record PollBody(string? DeviceCode);

    public sealed record DecideBody(bool Allow);

    public static void Map(RouteGroupBuilder api)
    {
        var link = api.MapGroup("/link");
        link.MapPost("/start", (StartBody body, HttpContext context, LinkService links, IPublicAddress address) =>
        {
            var started = links.Start(body.Device, body.App, context.Connection.RemoteIpAddress?.ToString() ?? "");
            if (started is null) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            var origin = address.Origin(context.Request);
            return Results.Ok(new
            {
                deviceCode = started.DeviceCode,
                userCode = started.UserCode,
                verifyUrl = $"{origin}/link",
                verifyUrlComplete = $"{origin}/link?code={started.UserCode}",
                expiresIn = (int)links.Lifetime.TotalSeconds
            });
        }).AllowAnonymous().RequireRateLimiting(AccountEndpoints.RateLimit);

        link.MapPost("/poll", async (PollBody body, LinkService links, CancellationToken cancel) =>
        {
            var result = await links.Poll(body.DeviceCode, cancel);
            return result.Status switch
            {
                PollStatus.Approved => Results.Ok(new { name = result.User, token = result.Token }),
                PollStatus.Pending => Results.StatusCode(StatusCodes.Status202Accepted),
                PollStatus.Denied => Results.StatusCode(StatusCodes.Status403Forbidden),
                _ => Results.StatusCode(StatusCodes.Status410Gone)
            };
        }).AllowAnonymous().RequireRateLimiting(PollLimit);

        link.MapGet("/{code}", (string code, LinkService links) =>
            links.Describe(code) is { State: LinkState.Pending } info ? Results.Ok(Describe(info)) : Results.NotFound());

        link.MapPost("/{code}", (string code, DecideBody body, ClaimsPrincipal user, LinkService links) =>
            links.Decide(code, user.UserName(), body.Allow) ? Results.NoContent() : Results.NotFound());

        var devices = api.MapGroup("/devices");
        devices.MapGet("/", async (ClaimsPrincipal user, AccountService accounts, CancellationToken cancel) =>
            Results.Ok((await accounts.Devices(user.UserName(), cancel)).Select(device => new
            {
                id = device.Id,
                name = device.Name,
                created = device.Created,
                lastUsed = device.LastUsed,
                current = device.Id == user.FindFirstValue(BearerSessionHandler.CredentialClaim)
            })));
        devices.MapPost("/{id}/revoke", async (string id, ClaimsPrincipal user, AccountService accounts, CancellationToken cancel) =>
            await accounts.RevokeDevice(user.UserName(), id, cancel) ? Results.NoContent() : Results.NotFound());
    }

    public static object Describe(LinkInfo info) =>
        new { code = info.UserCode, device = info.Device, app = info.App, address = info.Address, created = info.Created };
}
