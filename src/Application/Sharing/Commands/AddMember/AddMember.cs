using Application.Common.Interfaces;
using Application.Common.Security;

namespace Application.Sharing.Commands.AddMember;

/// <summary>The folder's owner adds someone by name. Not found when the folder isn't theirs or nobody has that name.</summary>
[Authorize(Policy = Policies.NoGuests)]
public record AddMemberCommand(string? Folder, string? Name) : IRequest;

public class AddMemberCommandValidator : AbstractValidator<AddMemberCommand>
{
    public AddMemberCommandValidator()
    {
        RuleFor(c => c.Folder).NotEmpty();
        RuleFor(c => c.Name).Must(ProtocolConstants.IsLabel);
    }
}

public class AddMemberCommandHandler(IApplicationDbContext context, IUser user, IIdentityService identity) : IRequestHandler<AddMemberCommand>
{
    public async Task Handle(AddMemberCommand request, CancellationToken cancellationToken)
    {
        var name = request.Name!.Trim().ToLowerInvariant();
        if (!await context.Folders.AnyAsync(f => f.Id == request.Folder && f.Owner == user.Id, cancellationToken) || await identity.FindAsync(name) is null)
            throw new NotFoundException(request.Folder!, nameof(Folder));
        if (await context.FolderMembers.AnyAsync(m => m.FolderId == request.Folder && m.UserId == name, cancellationToken)) return;
        context.FolderMembers.Add(new FolderMember { FolderId = request.Folder!, UserId = name });
        await context.SaveChangesAsync(cancellationToken);
    }
}
