using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Shared;

namespace Application.FunctionalTests.Infrastructure;

/// <summary>Penrill Server on the test PostgreSQL database, with files in a temporary data directory.</summary>
public sealed class ServerFactory(bool openRegistration = false) : WebApplicationFactory<Program>
{
    public string DataDirectory { get; } = Directory.CreateTempSubdirectory("penrill-test-").FullName;

    protected override void ConfigureWebHost(IWebHostBuilder builder) => builder
        .UseEnvironment("Testing")
        .UseSetting($"ConnectionStrings:{Services.Database}", FunctionalTestSetup.DatabaseConnection)
        .UseSetting("PENRILL_DATA_DIR", DataDirectory)
        .UseSetting("PENRILL_ADMIN_SOCKET", "off")
        .UseSetting("PENRILL_OPEN_REGISTRATION", openRegistration ? "true" : "false");

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        if (Directory.Exists(DataDirectory)) Directory.Delete(DataDirectory, recursive: true);
    }
}
