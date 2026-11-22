using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Penrill.Application.Accounts;
using Penrill.Application.Admin;
using Penrill.Application.Common.Behaviours;
using Penrill.Application.Common.Models;
using Penrill.Application.Linking;
using Penrill.Application.Live;

namespace Microsoft.Extensions.DependencyInjection;

public static class DependencyInjection
{
    /// <summary>
    /// The shared use cases and services. A host that adds use cases of its own passes their assembly in
    /// <paramref name="extraAssemblies"/>, so their handlers, validators and mappings join the same pipeline.
    /// </summary>
    public static void AddApplicationServices(this IHostApplicationBuilder builder, params Assembly[] extraAssemblies)
    {
        Assembly[] assemblies = [Assembly.GetExecutingAssembly(), .. extraAssemblies];

        builder.Services.AddAutoMapper(cfg =>
        {
            cfg.LicenseKey = builder.Configuration["AutoMapper:LicenseKey"];
            cfg.AddMaps(assemblies);
        });

        builder.Services.AddValidatorsFromAssemblies(assemblies);

        builder.Services.AddMediatR(cfg =>
        {
            cfg.LicenseKey = builder.Configuration["MediatR:LicenseKey"];
            cfg.RegisterServicesFromAssemblies(assemblies);
            cfg.AddOpenRequestPreProcessor(typeof(LoggingBehaviour<>));
            cfg.AddOpenBehavior(typeof(UnhandledExceptionBehaviour<,>));
            cfg.AddOpenBehavior(typeof(AuthorizationBehaviour<,>));
            cfg.AddOpenBehavior(typeof(ValidationBehaviour<,>));
            cfg.AddOpenBehavior(typeof(PerformanceBehaviour<,>));
        });

        var services = builder.Services;
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(new AccountOptions { OpenRegistration = builder.Configuration.GetValue("PENRILL_OPEN_REGISTRATION", false) });
        services.TryAddSingleton(new LinkOptions());
        services.TryAddSingleton(new RoomOptions());
        services.AddScoped<AccountService>();
        services.AddScoped<GrantService>();
        services.AddScoped<AdminService>();
        services.AddSingleton<LinkService>();
        services.AddSingleton<BoardLog>();
        services.AddSingleton<RoomRegistry>();
        services.AddSingleton<LiveConnections>();
        services.AddSingleton<LiveSessions>();
        services.AddHostedService<RoomJanitor>();
    }
}
