using Application.Common.Exceptions;
using Application.Common.Interfaces;
using Application.Common.Security;
using Application.Sharing.Queries.GetCommunity;
using Sync.Protocol;

namespace Application.Templates.Commands.PublishTemplate;

/// <summary>Shares a board file as a template with everyone (no folder) or with one folder the author is in.</summary>
[Authorize(Policy = Policies.NoGuests)]
public record PublishTemplateCommand(string? Name, string? Folder, byte[] Data) : IRequest<TemplateDto>;

public class PublishTemplateCommandValidator : AbstractValidator<PublishTemplateCommand>
{
    public PublishTemplateCommandValidator()
    {
        RuleFor(c => c.Name).Must(ProtocolConstants.IsLabel);
        RuleFor(c => c.Data).Must(data => TemplateFile.IsBoard(data)).WithMessage("Not a board file.");
    }
}

public class PublishTemplateCommandHandler(IApplicationDbContext context, IUser user, TimeProvider time) : IRequestHandler<PublishTemplateCommand, TemplateDto>
{
    public async Task<TemplateDto> Handle(PublishTemplateCommand request, CancellationToken cancellationToken)
    {
        var folder = request.Folder ?? "";
        if (folder != "" && !await context.FolderMembers.AnyAsync(m => m.FolderId == folder && m.UserId == user.Id, cancellationToken))
            throw new NotFoundException(folder, nameof(Folder));
        var template = new Template
        {
            Id = Guid.NewGuid().ToString("N"), Folder = folder, Author = user.Id!, Name = request.Name!.Trim(), Data = request.Data,
            Updated = time.GetUtcNow().ToUnixTimeMilliseconds()
        };
        context.Templates.Add(template);
        await context.SaveChangesAsync(cancellationToken);
        return TemplateDtos.From(template);
    }
}

internal static class TemplateDtos
{
    public static TemplateDto From(Template template) => new(template.Id, template.Name, template.Folder, template.Author, template.Updated);
}
