using Shared;

namespace Application.FunctionalTests;

/// <summary>Starts PostgreSQL once for all functional tests (TestAppHost), migrates/creates schema and prepares resets.</summary>
[SetUpFixture]
public class FunctionalTestSetup
{
    internal static string DatabaseConnection { get; private set; } = null!;
    internal static string DataDirectory { get; } = Directory.CreateTempSubdirectory("penrill-server-test-").FullName;
    internal static DatabaseResetter DbResetter { get; private set; } = null!;

    private static DistributedApplication? _app;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        if (Environment.GetEnvironmentVariable("PENRILL_TEST_DATABASE") is { Length: > 0 } database)
            DatabaseConnection = database;
        else
            await StartTestAppHost();

        await using (var factory = new ServerFactory())
            factory.CreateClient().Dispose();
        DbResetter = await DatabaseResetter.CreateAsync(DatabaseConnection);
    }

    private static async Task StartTestAppHost()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var cancellationToken = cts.Token;

        var builder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.TestAppHost>(
            args: [], configureBuilder: (options, _) => options.DisableDashboard = true, cancellationToken);
        builder.Configuration["ASPIRE_ALLOW_UNSECURED_TRANSPORT"] = "true";

        _app = await builder.BuildAsync(cancellationToken).WaitAsync(cancellationToken);
        await _app.StartAsync(cancellationToken).WaitAsync(cancellationToken);
        await _app.ResourceNotifications.WaitForResourceHealthyAsync(Services.Database, cancellationToken);

        DatabaseConnection = (await _app.GetConnectionStringAsync(Services.Database, cancellationToken))!;
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (DbResetter is not null) await DbResetter.DisposeAsync();
        if (_app is not null) await _app.DisposeAsync();
        if (Directory.Exists(DataDirectory)) Directory.Delete(DataDirectory, recursive: true);
    }
}
