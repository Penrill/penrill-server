using Application.Common.Interfaces;
using Application.Common.Security;

namespace Application.Templates.Commands.DeleteTemplate;

/// <summary>Stops sharing a template: its author may, and so may an admin. Copies people added keep their last version.</summary>
[Authorize]
public record DeleteTemplateCommand(string Id) : IRequest;

public class DeleteTemplateCommandHandler(IApplicationDbContext context, IUser user) : IRequestHandler<DeleteTemplateCommand>
{
    public async Task Handle(DeleteTemplateCommand request, CancellationToken cancellationToken)
    {
        var admin = user.Roles?.Any(role => role is nameof(Role.Admin) or nameof(Role.Owner)) == true;
        var me = user.Id;
        if (await context.Templates.Where(t => t.Id == request.Id && (admin || t.Author == me)).ExecuteDeleteAsync(cancellationToken) == 0)
            throw new NotFoundException(request.Id, nameof(Template));
    }
}
