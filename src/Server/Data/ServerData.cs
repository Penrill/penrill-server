using Microsoft.Data.Sqlite;

namespace Penrill.Server.Data;

/// <summary>
/// Where a self-hosted server keeps everything: <c>PENRILL_DATA_DIR</c>, or <c>data</c> next to the app.
/// The database is <c>penrill.db</c>, images are files under <c>assets</c>.
/// </summary>
public sealed class ServerData
{
    public ServerData(string directory)
    {
        Directory = Path.GetFullPath(directory);
        System.IO.Directory.CreateDirectory(Directory);
    }

    public string Directory { get; }

    public string Database => Path.Combine(Directory, "penrill.db");

    public string Assets => Path.Combine(Directory, "assets");

    public string ConnectionString => new SqliteConnectionStringBuilder { DataSource = Database, Pooling = true }.ToString();

    public static ServerData From(IConfiguration configuration, IHostEnvironment environment) =>
        new(configuration["PENRILL_DATA_DIR"] is { Length: > 0 } directory ? directory : Path.Combine(environment.ContentRootPath, "data"));
}
