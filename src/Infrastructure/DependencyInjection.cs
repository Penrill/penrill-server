using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Penrill.Application.Common.Interfaces;
using Penrill.Application.Common.Security;
using Penrill.Domain.Constants;
using Penrill.Domain.Enums;
using Penrill.Infrastructure.Assets;
using Penrill.Infrastructure.Data;
using Penrill.Infrastructure.Data.Interceptors;
using Penrill.Infrastructure.Identity;

namespace Microsoft.Extensions.DependencyInjection;

public static class DependencyInjection
{
    /// <summary>The Penrill model on the database <paramref name="useDatabase"/> picks (provider, connection, migrations assembly).</summary>
    public static void AddInfrastructureServices(this IHostApplicationBuilder builder, Action<IServiceProvider, DbContextOptionsBuilder> useDatabase) =>
        builder.AddInfrastructureServices<ApplicationDbContext>(useDatabase);

    /// <summary>
    /// As above, with a context of the host's own that derives from <see cref="ApplicationDbContext"/> and adds data
    /// only that host keeps.
    /// </summary>
    public static void AddInfrastructureServices<TContext>(this IHostApplicationBuilder builder, Action<IServiceProvider, DbContextOptionsBuilder> useDatabase)
        where TContext : ApplicationDbContext
    {
        var services = builder.Services;
        services.AddScoped<ISaveChangesInterceptor, AuditableEntityInterceptor>();
        services.AddScoped<ISaveChangesInterceptor, DispatchDomainEventsInterceptor>();

        services.AddDbContext<TContext>((sp, options) =>
        {
            options.AddInterceptors(sp.GetServices<ISaveChangesInterceptor>());
            useDatabase(sp, options);
        });
        if (typeof(TContext) != typeof(ApplicationDbContext))
            services.AddScoped<ApplicationDbContext>(provider => provider.GetRequiredService<TContext>());
        services.AddScoped<IApplicationDbContext>(provider => provider.GetRequiredService<TContext>());
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
            .AddEntityFrameworkStores<TContext>();

        services.TryAddSingleton(TimeProvider.System);
        services.AddTransient<IIdentityService, IdentityService>();
    }

    /// <summary>Keeps images as files under <paramref name="directory"/>.</summary>
    public static IServiceCollection AddFileAssetStore(this IServiceCollection services, string directory)
    {
        services.AddSingleton(new FileAssetOptions { Directory = directory });
        services.AddSingleton<IAssetStore, FileAssetStore>();
        return services;
    }
}
