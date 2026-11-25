using Microsoft.AspNetCore.Mvc.Filters;

namespace Web.Infrastructure;

/// <summary>
/// Refuses cross-site requests to the server's own <c>/web</c> endpoints: checks <c>Sec-Fetch-Site</c>
/// and requires the <see cref="Header"/> header on non-GET requests.
/// </summary>
public sealed class WebOriginFilter : IAsyncActionFilter
{
    public const string Header = "X-Penrill-Web";

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var request = context.HttpContext.Request;
        if (request.Headers["Sec-Fetch-Site"] is [{ } site] && site is not ("same-origin" or "none"))
        {
            context.Result = new StatusCodeResult(StatusCodes.Status403Forbidden);
            return;
        }
        if (!HttpMethods.IsGet(request.Method) && !request.Headers.ContainsKey(Header))
        {
            context.Result = new StatusCodeResult(StatusCodes.Status403Forbidden);
            return;
        }
        await next();
    }
}
