using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Application.Common.Interfaces;

namespace Application.Common.Behaviours;

/// <summary>Warns about requests slower than half a second. Long polls and live sessions do not pass through here.</summary>
public partial class PerformanceBehaviour<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    private readonly ILogger<TRequest> _logger;
    private readonly IUser _user;

    public PerformanceBehaviour(ILogger<TRequest> logger, IUser user)
    {
        _logger = logger;
        _user = user;
    }

    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var response = await next();
        var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        if (elapsed > 500 && request is not ILongRunningRequest)
            LogSlow(_logger, typeof(TRequest).Name, elapsed, _user.Id ?? string.Empty);
        return response;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Penrill Long Running Request: {Name} ({Elapsed:F0} milliseconds) {UserId}")]
    private static partial void LogSlow(ILogger logger, string name, double elapsed, string userId);
}

/// <summary>A request that waits on purpose, such as a long poll; it is not reported as slow.</summary>
public interface ILongRunningRequest;
