using Microsoft.AspNetCore.Http.HttpResults;
using Penrill.Application.Linking;
using Penrill.Application.Linking.Commands.AnswerLink;
using Penrill.Application.Linking.Commands.PollLink;
using Penrill.Application.Linking.Commands.StartLink;
using Penrill.Application.Linking.Queries.GetLinkRequest;

namespace Penrill.Web.Endpoints;

/// <summary>Device linking after RFC 8628: an editor starts a request, someone signed in approves it by its code, the editor's poll gets a token.</summary>
public class Link : IEndpointGroup
{
    public static string? RoutePrefix => "/api/link";

    public sealed record StartBody(string? Device, string? App);

    public sealed record PollBody(string? DeviceCode);

    public sealed record DecideBody(bool Allow);

    public static void Map(RouteGroupBuilder groupBuilder)
    {
        groupBuilder.RequireAuthorization();
        groupBuilder.MapPost(StartLink, "/start").AllowAnonymous().RequireRateLimiting(RateLimits.Account);
        groupBuilder.MapPost(PollLink, "/poll").AllowAnonymous().RequireRateLimiting(RateLimits.LinkPoll);
        groupBuilder.MapGet(GetLinkRequest, "/{code}");
        groupBuilder.MapPost(AnswerLink, "/{code}");
    }

    [EndpointSummary("Start linking a device")]
    public static async Task<Ok<StartResponse>> StartLink(ISender sender, StartBody body, HttpContext context, IPublicAddress address)
    {
        var started = await sender.Send(new StartLinkCommand(body.Device, body.App, context.Connection.RemoteIpAddress?.ToString() ?? ""));
        var origin = address.Origin(context.Request);
        return TypedResults.Ok(new StartResponse(started.DeviceCode, started.UserCode, $"{origin}/link", $"{origin}/link?code={started.UserCode}", started.ExpiresIn));
    }

    [EndpointSummary("Wait for the decision on a link request")]
    [EndpointDescription("200 with the token once approved, 202 while pending, 403 when denied, 410 when expired or already used.")]
    public static async Task<IResult> PollLink(ISender sender, PollBody body)
    {
        var result = await sender.Send(new PollLinkCommand(body.DeviceCode));
        return result.Status switch
        {
            PollStatus.Approved => TypedResults.Ok(new { name = result.User, token = result.Token }),
            PollStatus.Pending => TypedResults.StatusCode(StatusCodes.Status202Accepted),
            PollStatus.Denied => TypedResults.StatusCode(StatusCodes.Status403Forbidden),
            _ => TypedResults.StatusCode(StatusCodes.Status410Gone)
        };
    }

    [EndpointSummary("Which device waits under a code")]
    public static async Task<Ok<RequestResponse>> GetLinkRequest(ISender sender, string code)
    {
        var info = await sender.Send(new GetLinkRequestQuery(code));
        return TypedResults.Ok(new RequestResponse(info.UserCode, info.Device, info.App, info.Address, info.Created));
    }

    [EndpointSummary("Allow or deny a device")]
    public static async Task<NoContent> AnswerLink(ISender sender, string code, DecideBody body)
    {
        await sender.Send(new AnswerLinkCommand(code, body.Allow));
        return TypedResults.NoContent();
    }

    public sealed record StartResponse(string DeviceCode, string UserCode, string VerifyUrl, string VerifyUrlComplete, int ExpiresIn);

    public sealed record RequestResponse(string Code, string Device, string App, string Address, DateTimeOffset Created);
}
