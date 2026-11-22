using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;

namespace Penrill.Application.FunctionalTests.Infrastructure;

/// <summary>A Penrill Server on SQLite in a temporary data directory of its own, removed again on dispose.</summary>
public sealed class ServerFactory(bool openRegistration = false) : WebApplicationFactory<Program>
{
    public string DataDirectory { get; } = Directory.CreateTempSubdirectory("penrill-test-").FullName;

    protected override void ConfigureWebHost(IWebHostBuilder builder) => builder
        .UseEnvironment("Testing")
        .UseSetting("PENRILL_DATA_DIR", DataDirectory)
        .UseSetting("PENRILL_ADMIN_SOCKET", "off")
        .UseSetting("PENRILL_OPEN_REGISTRATION", openRegistration ? "true" : "false");

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        SqliteConnection.ClearAllPools();
        Directory.Delete(DataDirectory, recursive: true);
    }
}
