using Microsoft.AspNetCore.Http.HttpResults;
using Penrill.Application.Accounts;
using Penrill.Application.Accounts.Commands.AcceptInvite;
using Penrill.Application.Accounts.Commands.CreateInvite;
using Penrill.Application.Accounts.Commands.DeleteInvite;
using Penrill.Application.Accounts.Queries.CheckInvite;
using Penrill.Application.Accounts.Queries.GetInvites;

namespace Penrill.Web.Endpoints;

/// <summary>Invite links: admins create, list and revoke them; invited people check and redeem them.</summary>
public class Invites : IEndpointGroup
{
    public static string? RoutePrefix => "/api/invites";

    public sealed record CreateBody(string? Role, int? Uses, int? Days);

    public sealed record AcceptBody(string? Name, string? Password);

    public sealed record CreatedInviteResponse(string Id, string Token, string Url, string Role, int UsesLeft, DateTimeOffset? Expires, string CreatedBy);

    public static void Map(RouteGroupBuilder groupBuilder)
    {
        groupBuilder.RequireAuthorization();
        groupBuilder.MapGet(GetInvites);
        groupBuilder.MapPost(CreateInvite);
        groupBuilder.MapDelete(DeleteInvite, "/{id}");
        groupBuilder.MapGet(CheckInvite, "/{token}").AllowAnonymous().RequireRateLimiting(RateLimits.Account);
        groupBuilder.MapPost(AcceptInvite, "/{token}").AllowAnonymous().RequireRateLimiting(RateLimits.Account);
    }

    [EndpointSummary("List open invite links")]
    public static async Task<Ok<IReadOnlyList<InviteDto>>> GetInvites(ISender sender) =>
        TypedResults.Ok(await sender.Send(new GetInvitesQuery()));

    [EndpointSummary("Create an invite link")]
    [EndpointDescription("Admin or owner only. Defaults to a single-use member invite valid for 7 days.")]
    public static async Task<Ok<CreatedInviteResponse>> CreateInvite(ISender sender, HttpContext context, IPublicAddress address, CreateBody? input = null)
    {
        var created = await sender.Send(new CreateInviteCommand(input?.Role, input?.Uses, input?.Days));
        var url = $"{address.Origin(context.Request)}/invite/{created.Token}";
        return TypedResults.Ok(new CreatedInviteResponse(created.Id, created.Token, url, created.Role, created.UsesLeft, created.Expires, created.CreatedBy));
    }

    [EndpointSummary("Delete an invite link")]
    public static async Task<NoContent> DeleteInvite(ISender sender, string id)
    {
        await sender.Send(new DeleteInviteCommand(id));
        return TypedResults.NoContent();
    }

    [EndpointSummary("Check whether an invite link is valid")]
    [EndpointDescription("204 when the link is active, 404 when it is unknown, expired or used up.")]
    public static async Task<NoContent> CheckInvite(ISender sender, string token)
    {
        await sender.Send(new CheckInviteQuery(token));
        return TypedResults.NoContent();
    }

    [EndpointSummary("Create an account with an invite link and sign in")]
    [EndpointDescription("Works even when open registration is closed. 404 when expired or used, 409 when the name is taken, 400 when invalid.")]
    public static async Task<Ok<SignedIn>> AcceptInvite(ISender sender, string token, AcceptBody input) =>
        TypedResults.Ok(await sender.Send(new AcceptInviteCommand(token, input.Name, input.Password)));
}
