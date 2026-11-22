using MediatR.Pipeline;
using Microsoft.Extensions.Logging;
using Penrill.Application.Common.Interfaces;

namespace Penrill.Application.Common.Behaviours;

/// <summary>
/// Logs which request ran and for whom. The request itself is not logged: it may carry passwords,
/// tokens or whole boards.
/// </summary>
public partial class LoggingBehaviour<TRequest> : IRequestPreProcessor<TRequest>
    where TRequest : notnull
{
    private readonly ILogger _logger;
    private readonly IUser _user;

    public LoggingBehaviour(ILogger<TRequest> logger, IUser user)
    {
        _logger = logger;
        _user = user;
    }

    public Task Process(TRequest request, CancellationToken cancellationToken)
    {
        LogRequest(_logger, typeof(TRequest).Name, _user.Id ?? string.Empty);
        return Task.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Penrill Request: {Name} {UserId}")]
    private static partial void LogRequest(ILogger logger, string name, string userId);
}
