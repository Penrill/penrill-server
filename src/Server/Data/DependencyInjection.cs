using Microsoft.EntityFrameworkCore;
using Penrill.Application.Common.Interfaces;

namespace Penrill.Server.Data;

public static class DataExtensions
{
    /// <summary>The Penrill model on SQLite in the data directory, with images as files beside it.</summary>
    public static ServerData AddServerData(this WebApplicationBuilder builder)
    {
        var data = ServerData.From(builder.Configuration, builder.Environment);
        builder.Services.AddSingleton(data);
        builder.AddInfrastructureServices((_, options) =>
            options.UseSqlite(data.ConnectionString, sqlite => sqlite.MigrationsAssembly(typeof(ServerData).Assembly.GetName().Name)));
        builder.Services.AddFileAssetStore(data.Assets);
        builder.Services.AddSingleton<IBackupWriter, SqliteBackupWriter>();
        return data;
    }
}
