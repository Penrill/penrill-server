using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Penrill.Application.Common.Interfaces;
using Penrill.Domain.Constants;
using Penrill.Web.Services;

namespace Microsoft.Extensions.DependencyInjection;

public static class DependencyInjection
{
    /// <summary>Penrill in the browser lives on app.penrill.com (and penrill.com); the rest are local development editors.</summary>
    public const string DefaultEditorOrigins = "https://app.penrill.com,https://penrill.com,http://localhost:5174,http://127.0.0.1:5174,http://localhost:5173";

    /// <summary>
    /// Everything the /api endpoints need: the current user, bearer sessions, problem details, rate limits, CORS for the
    /// web editors (<c>PENRILL_EDITOR_ORIGINS</c> adds to <see cref="DefaultEditorOrigins"/>) and, with
    /// <c>PENRILL_TRUST_PROXY</c>, forwarded headers from a reverse proxy on this machine.
    /// </summary>
    public static void AddWebServices(this IHostApplicationBuilder builder)
    {
        var services = builder.Services;
        if (builder is WebApplicationBuilder web)
            web.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = BoardLimits.MaxBoardBytes);

        services.AddHttpContextAccessor();
        services.AddScoped<IUser, CurrentUser>();
        services.TryAddSingleton<IPublicAddress, RequestAddress>();
        services.AddExceptionHandler<ProblemDetailsExceptionHandler>();
        services.AddProblemDetails();
        services.Configure<ApiBehaviorOptions>(options => options.SuppressModelStateInvalidFilter = true);
        services.AddEndpointsApiExplorer();
        services.AddOpenApi(options => options.AddOperationTransformer<ApiExceptionOperationTransformer>());

        services.AddAuthentication(BearerSessionHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, BearerSessionHandler>(BearerSessionHandler.SchemeName, null);

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy(RateLimits.Account, context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(1) }));
            options.AddPolicy(RateLimits.LinkPoll, context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 240, Window = TimeSpan.FromMinutes(1) }));
        });

        // Behind a reverse proxy on this machine every client reaches Kestrel from the proxy's address, so the per-address
        // limits above would be one shared bucket. With PENRILL_TRUST_PROXY the proxy's X-Forwarded-For/-Proto are believed, from loopback only.
        services.Configure<ForwardedHeadersOptions>(options => options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto);

        var extraOrigins = builder.Configuration["PENRILL_EDITOR_ORIGINS"];
        var origins = string.IsNullOrWhiteSpace(extraOrigins) ? DefaultEditorOrigins : $"{DefaultEditorOrigins},{extraOrigins}";
        services.AddCors(options => options.AddDefaultPolicy(policy => policy
            .WithOrigins(origins.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToArray())
            .WithMethods("GET", "PUT", "POST", "DELETE")
            .WithHeaders("Content-Type", "Authorization")));
    }

    /// <summary>The middleware the API needs, in order, and every endpoint group under /api.</summary>
    public static WebApplication MapPenrillApi(this WebApplication app)
    {
        if (app.Configuration.GetValue("PENRILL_TRUST_PROXY", false)) app.UseForwardedHeaders();
        app.UseExceptionHandler();
        // Chrome asks before a public site such as app.penrill.com talks to a server on the local network
        // (Private Network Access); the preflight has to say the server agrees.
        app.Use((context, next) =>
        {
            if (HttpMethods.IsOptions(context.Request.Method) && context.Request.Headers.ContainsKey("Access-Control-Request-Private-Network"))
                context.Response.Headers["Access-Control-Allow-Private-Network"] = "true";
            return next(context);
        });
        app.UseCors();
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(15), KeepAliveTimeout = TimeSpan.FromSeconds(30) });
        app.UseRateLimiter();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapEndpoints(typeof(Penrill.Web.Endpoints.Boards).Assembly);
        return app;
    }
}
