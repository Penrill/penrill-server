using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Penrill.Api.Hosting;
using Penrill.Core.Boards;
using Penrill.Core.Sharing;

namespace Penrill.Api.Endpoints;

internal static class SharingEndpoints
{
    public sealed record FolderChange(string? Name, string? Folder, string? Board);

    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/community", async (ClaimsPrincipal user, ISharingStore sharing, CancellationToken cancel) =>
            Results.Ok(await sharing.Catalog(user.UserName(), cancel)));
        api.MapPost("/folders", async (FolderChange input, ClaimsPrincipal user, ISharingStore sharing, CancellationToken cancel) =>
        {
            if (!TemplateFile.IsLabel(input.Name)) return Results.BadRequest();
            await sharing.CreateFolder(user.UserName(), input.Name!, cancel);
            return Results.NoContent();
        });
        api.MapPost("/members", async (FolderChange input, ClaimsPrincipal user, ISharingStore sharing, CancellationToken cancel) =>
            !TemplateFile.IsLabel(input.Name) || input.Folder is null ? Results.BadRequest()
            : await sharing.AddMember(user.UserName(), input.Folder, input.Name!, cancel) ? Results.NoContent() : Results.NotFound());
        api.MapPost("/share-board", async (FolderChange input, ClaimsPrincipal user, ISharingStore sharing, CancellationToken cancel) =>
            input.Folder is null || input.Board is null || !BoardLimits.IsBoardId(input.Board) ? Results.BadRequest()
            : await sharing.ShareBoard(user.UserName(), input.Folder, input.Board, cancel) ? Results.NoContent() : Results.Conflict());
        api.MapPost("/templates", async (string name, string? folder, HttpRequest request, ClaimsPrincipal user, ITemplateStore templates, CancellationToken cancel) =>
        {
            if (!TemplateFile.IsLabel(name)) return Results.BadRequest();
            if (await Bodies.Read(request, BoardLimits.MaxBoardBytes, cancel) is not { } data) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            if (!TemplateFile.IsBoard(data)) return Results.BadRequest();
            return await templates.Publish(user.UserName(), folder ?? "", name, data, cancel) ? Results.NoContent() : Results.NotFound();
        });
        api.MapGet("/templates/{id}", async (string id, ClaimsPrincipal user, ITemplateStore templates, CancellationToken cancel) =>
            await templates.Find(user.UserName(), id, cancel) is { } data ? Results.Bytes(data, "application/json") : Results.NotFound());
    }
}
