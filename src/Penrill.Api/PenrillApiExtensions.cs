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
        services.AddSingleton<RoomRegistry>();
        services.AddSingleton<AccountService>();
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
        var origins = builder.Configuration["PENRILL_EDITOR_ORIGINS"] ?? "http://localhost:5174,http://127.0.0.1:5174,http://localhost:5173";
        services.AddCors(options => options.AddDefaultPolicy(policy => policy
            .WithOrigins(origins.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            .WithMethods("GET", "PUT", "POST")
            .WithHeaders("Content-Type", "Authorization")));
        return builder;
    }

    public static WebApplication MapPenrillApi(this WebApplication app)
    {
        app.UseCors();
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(15), KeepAliveTimeout = TimeSpan.FromSeconds(30) });
        app.UseRateLimiter();
        app.UseAuthentication();
        app.UseAuthorization();
        var api = app.MapGroup("/api").RequireAuthorization();
        AccountEndpoints.Map(api);
        BoardEndpoints.Map(api);
        AssetEndpoints.Map(api);
        SharingEndpoints.Map(api);
        LiveEndpoint.Map(api);
        return app;
    }
}
