using System.Security.Cryptography;
using Application.Common.Exceptions;
using Application.Common.Interfaces;
using Application.Common.Security;
using Sync.Live;

namespace Application.Boards.Commands.UploadAsset;

/// <summary>Stores an image of a board under the SHA-256 of its bytes. Images on one board together stay under the board quota.</summary>
[Authorize]
public record UploadAssetCommand(string BoardId, string Hash, string? ContentType, byte[] Content) : IRequest;

public class UploadAssetCommandValidator : AbstractValidator<UploadAssetCommand>
{
    public UploadAssetCommandValidator()
    {
        RuleFor(c => c.BoardId).MustBeBoardId();
        RuleFor(c => c.Hash).Must(ProtocolConstants.IsAssetHash).WithMessage("Not an asset hash.");
    }
}

public class UploadAssetCommandHandler(IApplicationDbContext context, IUser user, IAssetStore assets) : IRequestHandler<UploadAssetCommand>
{
    public static readonly IReadOnlySet<string> ImageTypes =
        new HashSet<string> { "image/png", "image/jpeg", "image/gif", "image/webp", "image/avif", "image/bmp", "image/svg+xml" };

    public async Task Handle(UploadAssetCommand request, CancellationToken cancellationToken)
    {
        await context.EnsureAccessAsync(user.Id!, request.BoardId, cancellationToken);
        var type = request.ContentType?.ToLowerInvariant();
        if (type is null || !ImageTypes.Contains(type)) throw new UnsupportedMediaTypeException();
        if (request.Content.Length > BoardLimitPolicy.SelfHostedDefault.MaxAssetBytes) throw new PayloadTooLargeException();
        if (Convert.ToHexStringLower(SHA256.HashData(request.Content)) != request.Hash) throw new BadRequestException("hash-mismatch");
        var known = await context.BoardAssets.AnyAsync(a => a.BoardId == request.BoardId && a.Hash == request.Hash, cancellationToken);
        if (!known)
        {
            var used = await context.BoardAssets.Where(a => a.BoardId == request.BoardId).SumAsync(a => a.Size, cancellationToken);
            if (used + request.Content.Length > BoardLimitPolicy.SelfHostedDefault.MaxBoardAssetBytes) throw new PayloadTooLargeException();
        }
        if (!await assets.ExistsAsync(request.Hash, cancellationToken)) await assets.SaveAsync(request.Hash, request.Content, cancellationToken);
        if (!known)
        {
            context.BoardAssets.Add(new BoardAsset { BoardId = request.BoardId, Hash = request.Hash, ContentType = type, Size = request.Content.Length });
            try
            {
                await context.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                // The same image arrived twice at once; one record is enough.
            }
        }
        SyncTelemetry.AssetBytes.Add(request.Content.Length);
    }
}
