using Microsoft.AspNetCore.Http.HttpResults;
using Penrill.Application.Sharing.Commands.AddBoardPerson;
using Penrill.Application.Sharing.Commands.AddMember;
using Penrill.Application.Sharing.Commands.CreateFolder;
using Penrill.Application.Sharing.Commands.DisableDrawLink;
using Penrill.Application.Sharing.Commands.EnableDrawLink;
using Penrill.Application.Sharing.Commands.JoinBoard;
using Penrill.Application.Sharing.Commands.RemoveBoardPerson;
using Penrill.Application.Sharing.Commands.ShareBoard;
using Penrill.Application.Sharing.Queries.GetBoardPeople;
using Penrill.Application.Sharing.Queries.GetCommunity;
using Penrill.Application.Sharing.Queries.GetDrawLink;

namespace Penrill.Web.Endpoints;

/// <summary>Folders, people on a board, draw links and joining by one.</summary>
public class Sharing : IEndpointGroup
{
    public static string? RoutePrefix => "/api";

    public sealed record FolderChange(string? Name, string? Folder, string? Board);

    public sealed record JoinBody(string? Token, string? Name);

    public static void Map(RouteGroupBuilder groupBuilder)
    {
        groupBuilder.RequireAuthorization();
        groupBuilder.MapGet(GetCommunity, "/community");
        groupBuilder.MapPost(CreateFolder, "/folders");
        groupBuilder.MapPost(AddMember, "/members");
        groupBuilder.MapPost(ShareBoard, "/share-board");
        groupBuilder.MapGet(GetBoardPeople, "/boards/{id}/people");
        groupBuilder.MapPost(AddBoardPerson, "/boards/{id}/people");
        groupBuilder.MapDelete(RemoveBoardPerson, "/boards/{id}/people/{name}");
        groupBuilder.MapGet(GetDrawLink, "/boards/{id}/link");
        groupBuilder.MapPost(EnableDrawLink, "/boards/{id}/link");
        groupBuilder.MapDelete(DisableDrawLink, "/boards/{id}/link");
        groupBuilder.MapPost(Join, "/join").AllowAnonymous().RequireRateLimiting(RateLimits.Account);
    }

    [EndpointSummary("Folders, their members and boards, and templates")]
    public static async Task<Ok<CommunityDto>> GetCommunity(ISender sender) => TypedResults.Ok(await sender.Send(new GetCommunityQuery()));

    [EndpointSummary("Make a folder")]
    public static async Task<NoContent> CreateFolder(ISender sender, FolderChange input)
    {
        await sender.Send(new CreateFolderCommand(input.Name));
        return TypedResults.NoContent();
    }

    [EndpointSummary("Add someone to a folder")]
    public static async Task<NoContent> AddMember(ISender sender, FolderChange input)
    {
        await sender.Send(new AddMemberCommand(input.Folder, input.Name));
        return TypedResults.NoContent();
    }

    [EndpointSummary("Share a board into a folder")]
    public static async Task<NoContent> ShareBoard(ISender sender, FolderChange input)
    {
        await sender.Send(new ShareBoardCommand(input.Folder, input.Board));
        return TypedResults.NoContent();
    }

    [EndpointSummary("People let onto a board by name")]
    public static async Task<Ok<IReadOnlyList<string>>> GetBoardPeople(ISender sender, string id) =>
        TypedResults.Ok(await sender.Send(new GetBoardPeopleQuery(id)));

    [EndpointSummary("Let someone onto a board")]
    public static async Task<NoContent> AddBoardPerson(ISender sender, string id, FolderChange input)
    {
        await sender.Send(new AddBoardPersonCommand(id, input.Name));
        return TypedResults.NoContent();
    }

    [EndpointSummary("Take someone off a board")]
    public static async Task<NoContent> RemoveBoardPerson(ISender sender, string id, string name)
    {
        await sender.Send(new RemoveBoardPersonCommand(id, name));
        return TypedResults.NoContent();
    }

    [EndpointSummary("The board's draw link")]
    public static async Task<Ok<DrawLinkDto>> GetDrawLink(ISender sender, string id) => TypedResults.Ok(await sender.Send(new GetDrawLinkQuery(id)));

    [EndpointSummary("Turn the draw link on")]
    public static async Task<Ok<DrawLinkDto>> EnableDrawLink(ISender sender, string id) => TypedResults.Ok(await sender.Send(new EnableDrawLinkCommand(id)));

    [EndpointSummary("Turn the draw link off")]
    public static async Task<NoContent> DisableDrawLink(ISender sender, string id)
    {
        await sender.Send(new DisableDrawLinkCommand(id));
        return TypedResults.NoContent();
    }

    [EndpointSummary("Open a board by its draw link")]
    public static async Task<Ok<JoinedDto>> Join(ISender sender, JoinBody input) =>
        TypedResults.Ok(await sender.Send(new JoinBoardCommand(input.Token, input.Name)));
}
