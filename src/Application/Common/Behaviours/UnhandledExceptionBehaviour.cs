using Microsoft.Extensions.Logging;
using ValidationException = Penrill.Application.Common.Exceptions.ValidationException;
using Penrill.Application.Common.Exceptions;

namespace Penrill.Application.Common.Behaviours;

/// <summary>Logs failures that are not one of the expected refusals (validation, access, conflicts and the like).</summary>
public partial class UnhandledExceptionBehaviour<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    private readonly ILogger<TRequest> _logger;

    public UnhandledExceptionBehaviour(ILogger<TRequest> logger)
    {
        _logger = logger;
    }

    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        try
        {
            return await next();
        }
        catch (Exception ex) when (!IsExpected(ex))
        {
            LogUnhandled(_logger, ex, typeof(TRequest).Name);
            throw;
        }
    }

    private static bool IsExpected(Exception ex) => ex is RequestException or ValidationException or ForbiddenAccessException
        or UnauthorizedAccessException or NotFoundException or OperationCanceledException;

    [LoggerMessage(Level = LogLevel.Error, Message = "Penrill Request: Unhandled Exception for Request {Name}")]
    private static partial void LogUnhandled(ILogger logger, Exception error, string name);
}
