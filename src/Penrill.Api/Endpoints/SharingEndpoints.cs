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
            await TemplateBody(name, request, cancel) is not { } data ? Results.BadRequest()
            : await templates.Publish(user.UserName(), folder ?? "", name, data, cancel) is { } info ? Results.Ok(info) : Results.NotFound());
        api.MapPut("/templates/{id}", async (string id, string name, HttpRequest request, ClaimsPrincipal user, ITemplateStore templates, CancellationToken cancel) =>
            await TemplateBody(name, request, cancel) is not { } data ? Results.BadRequest()
            : await templates.Update(user.UserName(), id, name, data, cancel) is { } info ? Results.Ok(info) : Results.NotFound());
        api.MapDelete("/templates/{id}", async (string id, ClaimsPrincipal user, ITemplateStore templates, CancellationToken cancel) =>
            await templates.Remove(user.UserName(), user.IsInRole("Admin") || user.IsInRole("Owner"), id, cancel) ? Results.NoContent() : Results.NotFound());
        api.MapGet("/templates/{id}", async (string id, ClaimsPrincipal user, ITemplateStore templates, CancellationToken cancel) =>
            await templates.Find(user.UserName(), id, cancel) is { } data ? Results.Bytes(data, "application/json") : Results.NotFound());
    }

    private static async Task<byte[]?> TemplateBody(string name, HttpRequest request, CancellationToken cancel) =>
        TemplateFile.IsLabel(name) && await Bodies.Read(request, BoardLimits.MaxBoardBytes, cancel) is { } data && TemplateFile.IsBoard(data) ? data : null;
}
