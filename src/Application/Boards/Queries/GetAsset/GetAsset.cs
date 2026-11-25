using Application.Common.Interfaces;
using Application.Common.Security;

namespace Application.Boards.Queries.GetAsset;

[Authorize]
public record GetAssetQuery(string BoardId, string Hash) : IRequest<AssetContent>;

/// <summary>An image to stream back; the caller disposes <see cref="Content"/>.</summary>
public sealed record AssetContent(Stream Content, string ContentType);

public class GetAssetQueryValidator : AbstractValidator<GetAssetQuery>
{
    public GetAssetQueryValidator() => RuleFor(q => q.BoardId).MustBeBoardId();
}

public class GetAssetQueryHandler(IApplicationDbContext context, IUser user, IAssetStore assets) : IRequestHandler<GetAssetQuery, AssetContent>
{
    public async Task<AssetContent> Handle(GetAssetQuery request, CancellationToken cancellationToken)
    {
        await context.EnsureAccessAsync(user.Id!, request.BoardId, cancellationToken);
        var info = ProtocolConstants.IsAssetHash(request.Hash)
            ? await context.BoardAssets.AsNoTracking().FirstOrDefaultAsync(a => a.BoardId == request.BoardId && a.Hash == request.Hash, cancellationToken)
            : null;
        Guard.Against.NotFound(request.Hash, info);
        var content = await assets.OpenReadAsync(request.Hash, cancellationToken);
        Guard.Against.NotFound(request.Hash, content);
        return new AssetContent(content, info.ContentType);
    }
}
