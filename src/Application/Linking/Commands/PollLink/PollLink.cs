using Penrill.Application.Common.Behaviours;

namespace Penrill.Application.Linking.Commands.PollLink;

/// <summary>The editor waits for the decision (long poll); an approved request hands out its device token once.</summary>
public record PollLinkCommand(string? DeviceCode) : IRequest<PollResult>, ILongRunningRequest;

public class PollLinkCommandHandler(LinkService links) : IRequestHandler<PollLinkCommand, PollResult>
{
    public Task<PollResult> Handle(PollLinkCommand request, CancellationToken cancellationToken) => links.Poll(request.DeviceCode, cancellationToken);
}
