using Application.Common.Security;

namespace Application.Linking.Queries.GetLinkRequest;

/// <summary>Which device waits under a code, for the approval page. Not found once it is decided or expired.</summary>
[Authorize]
public record GetLinkRequestQuery(string Code) : IRequest<LinkInfo>;

public class GetLinkRequestQueryHandler(LinkService links) : IRequestHandler<GetLinkRequestQuery, LinkInfo>
{
    public Task<LinkInfo> Handle(GetLinkRequestQuery request, CancellationToken cancellationToken)
    {
        var info = links.Describe(request.Code);
        if (info is not { State: LinkState.Pending }) throw new NotFoundException(request.Code, "link request");
        return Task.FromResult(info);
    }
}
