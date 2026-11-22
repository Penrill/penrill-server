using Penrill.Application.Common.Exceptions;

namespace Penrill.Application.Linking.Commands.StartLink;

/// <summary>An editor asks to be linked to an account; someone signed in approves it by the short code.</summary>
public record StartLinkCommand(string? Device, string? App, string Address) : IRequest<LinkStartDto>;

public sealed record LinkStartDto(string DeviceCode, string UserCode, int ExpiresIn);

public class StartLinkCommandHandler(LinkService links) : IRequestHandler<StartLinkCommand, LinkStartDto>
{
    public Task<LinkStartDto> Handle(StartLinkCommand request, CancellationToken cancellationToken)
    {
        var started = links.Start(request.Device, request.App, request.Address) ?? throw new ServiceUnavailableException();
        return Task.FromResult(new LinkStartDto(started.DeviceCode, started.UserCode, (int)links.Lifetime.TotalSeconds));
    }
}
