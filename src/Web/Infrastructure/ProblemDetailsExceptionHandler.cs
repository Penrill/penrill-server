using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Penrill.Application.Common.Exceptions;

namespace Penrill.Web.Infrastructure;

/// <summary>
/// Turns the refusals of use cases into RFC 9110 <see cref="ProblemDetails"/> with the status editors expect.
/// A <see cref="RequestException.Reason"/> goes into a <c>reason</c> member, which editors read
/// (for example <c>{"reason":"board-full"}</c>). Unrecognised exceptions fall through to the default middleware.
/// </summary>
public class ProblemDetailsExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        ProblemDetails? problem = exception switch
        {
            ValidationException ve => new ValidationProblemDetails(ve.Errors) { Status = StatusCodes.Status400BadRequest },
            NotFoundException => new ProblemDetails { Status = StatusCodes.Status404NotFound, Title = "The specified resource was not found." },
            UnauthorizedAccessException => new ProblemDetails { Status = StatusCodes.Status401Unauthorized, Title = "Unauthorized" },
            ForbiddenAccessException => new ProblemDetails { Status = StatusCodes.Status403Forbidden, Title = "Forbidden" },
            RequestException re => WithReason(new ProblemDetails { Status = StatusOf(re), Title = re.Message }, re.Reason),
            BadHttpRequestException bad => new ProblemDetails { Status = bad.StatusCode, Title = "Bad Request" },
            _ => null
        };
        if (problem is null) return false;
        problem.Type = $"https://tools.ietf.org/html/rfc9110#section-15.5.{problem.Status - 399}";
        httpContext.Response.StatusCode = problem.Status!.Value;
        await httpContext.Response.WriteAsJsonAsync(problem, problem.GetType(), options: null, contentType: "application/problem+json", cancellationToken);
        return true;
    }

    private static int StatusOf(RequestException exception) => exception switch
    {
        BadRequestException => StatusCodes.Status400BadRequest,
        ConflictException => StatusCodes.Status409Conflict,
        GoneException => StatusCodes.Status410Gone,
        PayloadTooLargeException => StatusCodes.Status413PayloadTooLarge,
        UnsupportedMediaTypeException => StatusCodes.Status415UnsupportedMediaType,
        ServiceUnavailableException => StatusCodes.Status503ServiceUnavailable,
        _ => StatusCodes.Status400BadRequest
    };

    private static ProblemDetails WithReason(ProblemDetails problem, string? reason)
    {
        if (reason is not null) problem.Extensions["reason"] = reason;
        return problem;
    }
}
