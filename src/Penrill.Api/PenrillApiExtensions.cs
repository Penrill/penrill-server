using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Penrill.Api.Endpoints;
using Penrill.Api.Hosting;
using Penrill.Api.Live;
using Penrill.Core.Accounts;
using Penrill.Core.Admin;
using Penrill.Core.Linking;
using Penrill.Core.Boards;
using Penrill.Core.Rooms;

namespace Penrill.Api;

public static class PenrillApiExtensions
{
    public static WebApplicationBuilder AddPenrillApi(this WebApplicationBuilder builder)
    {
        builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = BoardLimits.MaxBoardBytes);
        var services = builder.Services;
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(new RoomOptions());
        services.TryAddSingleton<IPasswordHashing, IdentityPasswordHashing>();
        services.TryAddSingleton<IPublicAddress, RequestAddress>();
        services.TryAddSingleton(new AccountOptions { OpenRegistration = builder.Configuration.GetValue("PENRILL_OPEN_REGISTRATION", false) });
        services.TryAddSingleton(new LinkOptions());
        services.AddSingleton<RoomRegistry>();
        services.AddSingleton<AccountService>();
        services.AddSingleton<GrantService>();
        services.AddSingleton<LinkService>();
        services.AddSingleton<AdminService>();
        services.AddSingleton<LiveConnections>();
        services.AddHostedService<RoomJanitor>();
        services.AddAuthentication(BearerSessionHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, BearerSessionHandler>(BearerSessionHandler.SchemeName, null);
        services.AddAuthorization();
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy(AccountEndpoints.RateLimit, context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(1) }));
        });
        // Penrill in the browser lives on penrill.com; the rest are local development editors.
        var origins = builder.Configuration["PENRILL_EDITOR_ORIGINS"] ?? "https://penrill.com,http://localhost:5174,http://127.0.0.1:5174,http://localhost:5173";
        services.AddCors(options => options.AddDefaultPolicy(policy => policy
            .WithOrigins(origins.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            .WithMethods("GET", "PUT", "POST", "DELETE")
            .WithHeaders("Content-Type", "Authorization")));
        return builder;
    }

    public static WebApplication MapPenrillApi(this WebApplication app)
    {
        // Chrome asks before a public site such as penrill.com talks to a server on the local network
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
        var api = app.MapGroup("/api").RequireAuthorization();
        AccountEndpoints.Map(api);
        LinkEndpoints.Map(api);
        BoardEndpoints.Map(api);
        AssetEndpoints.Map(api);
        SharingEndpoints.Map(api);
        LiveEndpoint.Map(api);
        return app;
    }
}
