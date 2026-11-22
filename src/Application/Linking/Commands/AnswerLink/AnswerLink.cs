using Penrill.Application.Common.Interfaces;
using Penrill.Application.Common.Security;

namespace Penrill.Application.Linking.Commands.AnswerLink;

/// <summary>The signed-in person allows or denies the device waiting under the code.</summary>
[Authorize]
public record AnswerLinkCommand(string Code, bool Allow) : IRequest;

public class AnswerLinkCommandHandler(LinkService links, IUser user) : IRequestHandler<AnswerLinkCommand>
{
    public Task Handle(AnswerLinkCommand request, CancellationToken cancellationToken)
    {
        if (!links.Decide(request.Code, user.Id!, request.Allow)) throw new NotFoundException(request.Code, "link request");
        return Task.CompletedTask;
    }
}
