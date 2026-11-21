using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Penrill.Core.Accounts;
using Penrill.Core.Boards;
using Penrill.Core.Sharing;

namespace Penrill.Storage.Sqlite;

public static class SqliteStorageExtensions
{
    public static IServiceCollection AddSqliteStorage(this IServiceCollection services)
    {
        services.AddOptions<SqliteStorageOptions>().Configure<IConfiguration, IHostEnvironment>((options, configuration, environment) =>
            options.DataDirectory = configuration["PENRILL_DATA_DIR"] is { Length: > 0 } directory
                ? directory
                : Path.Combine(environment.ContentRootPath, "data"));
        services.AddSingleton<SqliteDatabase>();
        services.AddHostedService<SqliteStartup>();
        services.AddSingleton<IBoardStore, SqliteBoardStore>();
        services.AddSingleton<IAccountStore, SqliteAccountStore>();
        services.AddSingleton<IAssetStore, FileAssetStore>();
        services.AddSingleton<SqliteSharingStore>();
        services.AddSingleton<IBoardAccess>(provider => provider.GetRequiredService<SqliteSharingStore>());
        services.AddSingleton<ISharingStore>(provider => provider.GetRequiredService<SqliteSharingStore>());
        services.AddSingleton<ITemplateStore>(provider => provider.GetRequiredService<SqliteSharingStore>());
        return services;
    }

    private sealed class SqliteStartup(SqliteDatabase database) : IHostedService
    {
        public Task StartAsync(CancellationToken cancel) => Task.FromResult(database.DataDirectory);

        public Task StopAsync(CancellationToken cancel) => Task.CompletedTask;
    }
}
