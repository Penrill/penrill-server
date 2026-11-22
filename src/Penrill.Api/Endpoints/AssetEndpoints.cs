using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Net.Http.Headers;
using Penrill.Api.Hosting;
using Penrill.Core.Boards;
using Penrill.Core.Telemetry;

namespace Penrill.Api.Endpoints;

internal static class AssetEndpoints
{
    private static readonly HashSet<string> ImageTypes =
        ["image/png", "image/jpeg", "image/gif", "image/webp", "image/avif", "image/bmp", "image/svg+xml"];

    public static void Map(RouteGroupBuilder api)
    {
        api.MapPut("/boards/{id}/assets/{hash}", Upload);
        api.MapGet("/boards/{id}/assets/{hash}", Download);
    }

    private static async Task<IResult> Upload(
        string id, string hash, HttpRequest request, ClaimsPrincipal user, IBoardAccess access, IBoardStore store, IAssetStore assets, CancellationToken cancel)
    {
        if (await BoardGuard.Check(access, user.UserName(), id, cancel) is { } denied) return denied;
        if (!BoardLimits.IsAssetHash(hash)) return Results.BadRequest();
        var type = MediaTypeHeaderValue.TryParse(request.ContentType, out var media) ? media.MediaType.Value?.ToLowerInvariant() : null;
        if (type is null || !ImageTypes.Contains(type)) return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
        if (await Bodies.Read(request, BoardLimits.MaxAssetBytes, cancel) is not { } bytes) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != hash) return Results.BadRequest(new { reason = "hash-mismatch" });
        if (await store.FindAsset(id, hash, cancel) is null && await store.AssetBytes(id, cancel) + bytes.Length > BoardLimits.MaxBoardAssetBytes)
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        if (!await assets.Exists(hash, cancel)) await assets.Save(hash, bytes, cancel);
        await store.AddAsset(id, new AssetInfo(hash, type, bytes.Length), cancel);
        PenrillTelemetry.AssetBytes.Add(bytes.Length);
        return Results.NoContent();
    }

    private static async Task<IResult> Download(
        string id, string hash, HttpContext context, ClaimsPrincipal user, IBoardAccess access, IBoardStore store, IAssetStore assets, CancellationToken cancel)
    {
        if (await BoardGuard.Check(access, user.UserName(), id, cancel) is { } denied) return denied;
        if (!BoardLimits.IsAssetHash(hash) || await store.FindAsset(id, hash, cancel) is not { } info || assets.OpenRead(hash) is not { } content)
            return Results.NotFound();
        var headers = context.Response.Headers;
        headers.CacheControl = "private, max-age=31536000, immutable";
        headers.XContentTypeOptions = "nosniff";
        headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
        return Results.Stream(content, info.ContentType);
    }
}
