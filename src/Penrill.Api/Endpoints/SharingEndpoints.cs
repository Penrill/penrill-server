using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Penrill.Api.Hosting;
using Penrill.Core.Accounts;
using Penrill.Core.Boards;
using Penrill.Core.Sharing;

namespace Penrill.Api.Endpoints;

internal static class SharingEndpoints
{
    public sealed record FolderChange(string? Name, string? Folder, string? Board);
    public sealed record Join(string? Token, string? Name);

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
        api.MapGet("/boards/{id}/people", async (string id, ClaimsPrincipal user, ISharingStore sharing, CancellationToken cancel) =>
            !BoardLimits.IsBoardId(id) ? Results.BadRequest()
            : await sharing.People(user.UserName(), id, cancel) is { } people ? Results.Ok(people) : Results.NotFound());
        api.MapPost("/boards/{id}/people", async (string id, FolderChange input, ClaimsPrincipal user, ISharingStore sharing, CancellationToken cancel) =>
            !BoardLimits.IsBoardId(id) || !TemplateFile.IsLabel(input.Name) ? Results.BadRequest()
            : await sharing.AddPerson(user.UserName(), id, input.Name!, cancel) ? Results.NoContent() : Results.NotFound());
        api.MapDelete("/boards/{id}/people/{name}", async (string id, string name, ClaimsPrincipal user, ISharingStore sharing, CancellationToken cancel) =>
            !BoardLimits.IsBoardId(id) ? Results.BadRequest()
            : await sharing.RemovePerson(user.UserName(), id, name, cancel) ? Results.NoContent() : Results.NotFound());
        api.MapGet("/boards/{id}/link", async (string id, ClaimsPrincipal user, ISharingStore sharing, CancellationToken cancel) =>
            !BoardLimits.IsBoardId(id) ? Results.BadRequest()
            : await sharing.BoardLink(user.UserName(), id, cancel) is { } token ? Results.Ok(new { token }) : Results.NotFound());
        api.MapDelete("/boards/{id}/link", async (string id, ClaimsPrincipal user, ISharingStore sharing, CancellationToken cancel) =>
            !BoardLimits.IsBoardId(id) ? Results.BadRequest()
            : await sharing.RevokeBoardLink(user.UserName(), id, cancel) ? Results.NoContent() : Results.NotFound());
        // Anyone with the link may draw on the board: they get an account of their own that can reach nothing else.
        api.MapPost("/join", async (Join input, ISharingStore sharing, AccountService accounts, CancellationToken cancel) =>
        {
            if (string.IsNullOrWhiteSpace(input.Token) || input.Token.Length > 64
                || await sharing.LinkedBoard(input.Token, cancel) is not { } board) return Results.NotFound();
            var label = new string((input.Name ?? "").ToLowerInvariant().Where(c => char.IsAsciiLetterOrDigit(c)).Take(20).ToArray());
            if (label.Length < 2) label = "guest";
            for (var attempt = 0; attempt < 5; attempt++)
            {
                var name = $"{label}-{Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(2))}";
                var created = await accounts.CreateAccount(name, AccountService.NewToken(), Penrill.Core.Accounts.Role.Member, cancel);
                if (created.Status != SignInStatus.Ok) continue;
                await sharing.AddGuest(input.Token, name, cancel);
                return Results.Ok(new { name, token = await accounts.IssueDeviceToken(name, "Guest", cancel), board });
            }
            return Results.Conflict();
        }).AllowAnonymous().RequireRateLimiting(AccountEndpoints.RateLimit);
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
