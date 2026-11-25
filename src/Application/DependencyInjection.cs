using System.Reflection;
using Application.Accounts;
using Application.Admin;
using Application.Common.Behaviours;
using Application.Common.Models;
using Application.Linking;
using Application.Live;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Sync;

namespace Microsoft.Extensions.DependencyInjection;

public static class ApplicationDependencyInjection
{
    public static void AddApplicationServices(this IHostApplicationBuilder builder, params Assembly[] extraAssemblies)
    {
        Assembly[] assemblies = [Assembly.GetExecutingAssembly(), .. extraAssemblies];

        builder.Services.AddValidatorsFromAssemblies(assemblies);

        builder.Services.AddMediatR(cfg =>
        {
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
        services.AddScoped<AccountService>();
        services.AddScoped<GrantService>();
        services.AddScoped<AdminService>();
        services.AddSingleton<LinkService>();
        services.AddBoardSync();
        services.AddHostedService<ExpiredSessionCleaner>();
    }
}
