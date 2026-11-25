using System.Threading.RateLimiting;
using Application.Common.Interfaces;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Web.Services;

namespace Microsoft.Extensions.DependencyInjection;

public static class WebDependencyInjection
{
    /// <summary>Penrill in the browser lives on app.penrill.com (and penrill.com); the rest are local development editors.</summary>
    public const string DefaultEditorOrigins = "https://app.penrill.com,https://penrill.com,http://localhost:5174,http://127.0.0.1:5174,http://localhost:5173";

    public static void AddWebServices(this IHostApplicationBuilder builder)
    {
        var services = builder.Services;
        if (builder is WebApplicationBuilder web)
            web.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = BoardLimitPolicy.SelfHostedDefault.MaxBoardBytes);

        services.AddHttpContextAccessor();
        services.AddScoped<IUser, CurrentUser>();
        services.AddScoped<WebOriginFilter>();
        services.TryAddSingleton<IPublicAddress, RequestAddress>();
        services.AddExceptionHandler<ProblemDetailsExceptionHandler>();
        services.AddProblemDetails();
        services.AddControllers();
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

        services.Configure<ForwardedHeadersOptions>(options => options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto);

        var extraOrigins = builder.Configuration["PENRILL_EDITOR_ORIGINS"];
        var origins = string.IsNullOrWhiteSpace(extraOrigins) ? DefaultEditorOrigins : $"{DefaultEditorOrigins},{extraOrigins}";
        services.AddCors(options => options.AddDefaultPolicy(policy => policy
            .WithOrigins(origins.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToArray())
            .WithMethods("GET", "PUT", "POST", "DELETE")
            .WithHeaders("Content-Type", "Authorization")));
    }

    public static WebApplication UsePenrillApiPipeline(this WebApplication app)
    {
        if (app.Configuration.GetValue("PENRILL_TRUST_PROXY", false)) app.UseForwardedHeaders();
        app.UseExceptionHandler();
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
        return app;
    }
}
