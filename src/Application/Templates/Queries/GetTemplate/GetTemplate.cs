using Application.Common.Interfaces;
using Application.Common.Security;

namespace Application.Templates.Queries.GetTemplate;

/// <summary>The board file of a template the user can see.</summary>
[Authorize]
public record GetTemplateQuery(string Id) : IRequest<byte[]>;

public class GetTemplateQueryHandler(IApplicationDbContext context, IUser user) : IRequestHandler<GetTemplateQuery, byte[]>
{
    public async Task<byte[]> Handle(GetTemplateQuery request, CancellationToken cancellationToken)
    {
        var me = user.Id!;
        var mine = context.FolderMembers.Where(m => m.UserId == me).Select(m => m.FolderId);
        var data = await context.Templates.AsNoTracking()
            .Where(t => t.Id == request.Id && (t.Folder == "" || mine.Contains(t.Folder)))
            .Select(t => t.Data)
            .FirstOrDefaultAsync(cancellationToken);
        Guard.Against.NotFound(request.Id, data);
        return data;
    }
}
