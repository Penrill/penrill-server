using Application.Common.Interfaces;
using Application.Common.Security;
using Application.Sharing.Queries.GetCommunity;
using Application.Templates.Commands.PublishTemplate;
using Sync.Protocol;

namespace Application.Templates.Commands.UpdateTemplate;

/// <summary>The author edits a shared template in place: everyone who added it gets the new version.</summary>
[Authorize(Policy = Policies.NoGuests)]
public record UpdateTemplateCommand(string Id, string? Name, byte[] Data) : IRequest<TemplateDto>;

public class UpdateTemplateCommandValidator : AbstractValidator<UpdateTemplateCommand>
{
    public UpdateTemplateCommandValidator()
    {
        RuleFor(c => c.Name).Must(ProtocolConstants.IsLabel);
        RuleFor(c => c.Data).Must(data => TemplateFile.IsBoard(data)).WithMessage("Not a board file.");
    }
}

public class UpdateTemplateCommandHandler(IApplicationDbContext context, IUser user, TimeProvider time) : IRequestHandler<UpdateTemplateCommand, TemplateDto>
{
    public async Task<TemplateDto> Handle(UpdateTemplateCommand request, CancellationToken cancellationToken)
    {
        var template = await context.Templates.FirstOrDefaultAsync(t => t.Id == request.Id && t.Author == user.Id, cancellationToken);
        Guard.Against.NotFound(request.Id, template);
        template.Name = request.Name!.Trim();
        template.Data = request.Data;
        // Never older than the version it replaces, even if the clock went back.
        template.Updated = Math.Max(template.Updated + 1, time.GetUtcNow().ToUnixTimeMilliseconds());
        await context.SaveChangesAsync(cancellationToken);
        return TemplateDtos.From(template);
    }
}
