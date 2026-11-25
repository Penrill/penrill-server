using Microsoft.AspNetCore.Server.Kestrel.Core;
using System.Net;
using Web.Cli;

namespace Web.Hosting;

public static class ServerHosting
{
    public const int DefaultPort = 5081;

    public static WebApplicationBuilder AddPenrillServer(this WebApplicationBuilder builder)
    {
        if (!Directory.Exists(Path.Combine(builder.Environment.ContentRootPath, "wwwroot")) &&
            Path.Combine(AppContext.BaseDirectory, "wwwroot") is var bundledRoot &&
            Directory.Exists(bundledRoot))
        {
            builder.Environment.WebRootPath = bundledRoot;
            builder.Environment.WebRootFileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(bundledRoot);
        }
        var configuration = builder.Configuration;
        var dataDirectory = configuration["PENRILL_DATA_DIR"] is { Length: > 0 } directory
            ? directory
            : Path.Combine(builder.Environment.ContentRootPath, "data");
        var port = configuration.GetValue("PENRILL_PORT", DefaultPort);
        var lan = string.Equals(configuration["PENRILL_ADDRESS_MODE"], "lan", StringComparison.OrdinalIgnoreCase);
        var urls = (configuration["urls"] ?? $"http://127.0.0.1:{port}").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var socket = configuration["PENRILL_ADMIN_SOCKET"] == "off" ? null : CommandLine.SocketPath(configuration, dataDirectory);
        if (socket is not null && System.Text.Encoding.UTF8.GetByteCount(Path.GetFullPath(socket)) > 100)
        {
            Console.Error.WriteLine($"Admin socket path too long, the penrill-server commands are unavailable: {socket}. Set PENRILL_ADMIN_SOCKET to a shorter path.");
            socket = null;
        }

        ServerAddress address;
        if (lan)
        {
            var certificate = new ServerCertificate(dataDirectory);
            address = new ServerAddress(AddressMode.Lan, "", port, certificate);
            configuration["AllowedHosts"] = "*";
            builder.WebHost.ConfigureKestrel(kestrel => kestrel.ListenAnyIP(port, listen => listen.UseHttps(https => https.ServerCertificateSelector = (_, host) => certificate.SelectCertificate(host))));
        }
        else
        {
            address = new ServerAddress(AddressMode.Local, urls[0].TrimEnd('/'), port, null);
            builder.WebHost.ConfigureKestrel(kestrel =>
            {
                foreach (var url in urls) Listen(kestrel, new Uri(url));
            });
        }
        if (socket is not null)
            builder.WebHost.ConfigureKestrel(kestrel =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(socket)!);
                File.Delete(socket);
                kestrel.ListenUnixSocket(socket, listen => listen.Use(next => connection =>
                {
                    connection.Features.Set(new AdminSocketFeature());
                    return next(connection);
                }));
            });

        var services = builder.Services;
        services.AddSingleton(address);
        services.AddSingleton<IPublicAddress>(address);
        services.AddSingleton(new ConnectLinks(configuration["PENRILL_CONNECT_PAGE"] is { Length: > 0 } page ? page : ConnectLinks.DefaultPage));
        services.AddSingleton<DesecClient>();
        services.AddSingleton<DuckDnsClient>();
        services.AddSingleton<AcmeClient>();
        services.AddSingleton<DomainManager>();
        services.AddHostedService(provider => provider.GetRequiredService<DomainManager>());
        services.AddSingleton<SetupAnnouncer>();
        services.AddHostedService(provider => provider.GetRequiredService<SetupAnnouncer>());
        services.AddHostedService(provider => new AdminSocketPermissions(socket, provider.GetRequiredService<IHostApplicationLifetime>()));
        services.AddAuthentication().AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, WebSessionHandler>(WebSessionHandler.SchemeName, null);
        services.AddAuthorizationBuilder()
            .AddPolicy(WebSessionHandler.SchemeName, policy => policy.AddAuthenticationSchemes(WebSessionHandler.SchemeName).RequireAuthenticatedUser())
            .AddPolicy(WebSessionHandler.AdminPolicy, policy => policy.AddAuthenticationSchemes(WebSessionHandler.SchemeName).RequireRole("Admin", "Owner"));
        return builder;
    }

    public static WebApplication MapPenrillServer(this WebApplication app)
    {
        app.UseStaticFiles();
        app.MapControllers();
        AdminCommands.Map(app);
        app.MapFallback(async context =>
        {
            var path = context.Request.Path;
            var index = app.Environment.WebRootFileProvider.GetFileInfo("index.html");
            if (path.StartsWithSegments("/api") || path.StartsWithSegments("/web") || Path.HasExtension(path.Value) || !index.Exists)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.Headers.CacheControl = "no-cache";
            await context.Response.SendFileAsync(index);
        }).AllowAnonymous();
        return app;
    }

    private static void Listen(KestrelServerOptions kestrel, Uri url)
    {
        Action<ListenOptions> configure = url.Scheme == "https" ? listen => listen.UseHttps() : _ => { };
        if (url.Host is "localhost") kestrel.ListenLocalhost(url.Port, configure);
        else if (url.Host is "*" or "+" or "0.0.0.0" or "[::]") kestrel.ListenAnyIP(url.Port, configure);
        else kestrel.Listen(IPAddress.Parse(url.Host.Trim('[', ']')), url.Port, configure);
    }

    private sealed class AdminSocketPermissions(string? socket, IHostApplicationLifetime lifetime) : IHostedService
    {
        public Task StartAsync(CancellationToken cancel)
        {
            if (socket is not null && !OperatingSystem.IsWindows())
                lifetime.ApplicationStarted.Register(() =>
                {
                    if (!OperatingSystem.IsWindows() && File.Exists(socket)) File.SetUnixFileMode(socket, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                });
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancel) => Task.CompletedTask;
    }
}
