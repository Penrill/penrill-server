using Application.Common.Interfaces;
using Application.Common.Security;
using Infrastructure.Assets;
using Infrastructure.Data;
using Infrastructure.Data.Interceptors;
using Infrastructure.Identity;
using Infrastructure.Sync;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Microsoft.Extensions.DependencyInjection;

public static class InfrastructureDependencyInjection
{
    public static ServerData AddInfrastructureServices(this IHostApplicationBuilder builder)
    {
        var data = ServerData.From(builder.Configuration, builder.Environment);
        var connectionString = builder.Configuration.GetConnectionString(Services.Database);
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = "Host=127.0.0.1;Port=5432;Database=penrill;Username=penrill;Password=penrill";

        var services = builder.Services;
        services.AddSingleton(data);
        services.AddScoped<ISaveChangesInterceptor, AuditableEntityInterceptor>();
        services.AddScoped<ISaveChangesInterceptor, DispatchDomainEventsInterceptor>();

        services.AddDbContext<ApplicationDbContext>((sp, options) =>
        {
            options.AddInterceptors(sp.GetServices<ISaveChangesInterceptor>());
            options.UseNpgsql(connectionString, npgsql => npgsql.MigrationsAssembly(typeof(ApplicationDbContext).Assembly.GetName().Name));
        });
        builder.EnrichNpgsqlDbContext<ApplicationDbContext>(settings => settings.DisableRetry = true);
        services.AddScoped<IApplicationDbContext>(provider => provider.GetRequiredService<ApplicationDbContext>());
        services.AddScoped<ApplicationDbContextInitialiser>();

        services.AddAuthorizationBuilder()
            .AddPolicy(Policies.NoGuests, policy => policy.RequireAssertion(context => !context.User.IsInRole(nameof(Role.Guest))));

        services
            .AddIdentityCore<ApplicationUser>(options =>
            {
                options.Password.RequiredLength = AccountRules.MinPassword;
                options.Password.RequireDigit = false;
                options.Password.RequireLowercase = false;
                options.Password.RequireUppercase = false;
                options.Password.RequireNonAlphanumeric = false;
                options.Password.RequiredUniqueChars = 1;
                options.User.AllowedUserNameCharacters = "abcdefghijklmnopqrstuvwxyz0123456789_-";
            })
            .AddRoles<IdentityRole>()
            .AddEntityFrameworkStores<ApplicationDbContext>();

        services.TryAddSingleton(TimeProvider.System);
        services.AddTransient<IIdentityService, IdentityService>();
        services.AddFileAssetStore(data.Assets);
        services.AddSingleton<IBackupWriter>(_ => new PostgresBackupWriter(data, connectionString));

        services.AddSingleton<IBoardStore, PostgresBoardStore>();
        services.AddSingleton<ILiveAuthenticator, ServerLiveAuthenticator>();
        services.AddSingleton<ILiveAccessPolicy, ServerLiveAccessPolicy>();

        return data;
    }

    public static IServiceCollection AddFileAssetStore(this IServiceCollection services, string directory)
    {
        services.AddSingleton(new FileAssetOptions { Directory = directory });
        services.AddSingleton<IAssetStore, FileAssetStore>();
        return services;
    }
}
