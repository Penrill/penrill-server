using Penrill.Application.Common.Interfaces;
using Penrill.Application.Common.Security;

namespace Penrill.Application.Sharing.Commands.CreateFolder;

/// <summary>Makes a folder with the signed-in user as its owner and first member.</summary>
[Authorize(Policy = Policies.NoGuests)]
public record CreateFolderCommand(string? Name) : IRequest<string>;

public class CreateFolderCommandValidator : AbstractValidator<CreateFolderCommand>
{
    public CreateFolderCommandValidator() => RuleFor(c => c.Name).Must(BoardLimits.IsLabel);
}

public class CreateFolderCommandHandler(IApplicationDbContext context, IUser user) : IRequestHandler<CreateFolderCommand, string>
{
    public async Task<string> Handle(CreateFolderCommand request, CancellationToken cancellationToken)
    {
        var folder = new Folder { Id = Guid.NewGuid().ToString("N"), Name = request.Name!.Trim(), Owner = user.Id! };
        context.Folders.Add(folder);
        context.FolderMembers.Add(new FolderMember { FolderId = folder.Id, UserId = folder.Owner });
        await context.SaveChangesAsync(cancellationToken);
        return folder.Id;
    }
}
