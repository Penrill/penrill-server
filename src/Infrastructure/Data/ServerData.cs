using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Infrastructure.Data;

/// <summary>
/// Where a self-hosted server keeps local files (images, TLS keys and certificates):
/// <c>PENRILL_DATA_DIR</c>, or <c>data</c> next to the app. Database data lives in PostgreSQL.
/// </summary>
public sealed class ServerData
{
    public ServerData(string directory)
    {
        Directory = Path.GetFullPath(directory);
        System.IO.Directory.CreateDirectory(Directory);
    }

    public string Directory { get; }

    public string Assets => Path.Combine(Directory, "assets");

    public static ServerData From(IConfiguration configuration, IHostEnvironment environment) =>
        new(configuration["PENRILL_DATA_DIR"] is { Length: > 0 } directory ? directory : Path.Combine(environment.ContentRootPath, "data"));
}
