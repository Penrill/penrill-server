using Shared;

namespace TestAppHost;

/// <summary>What the integration and functional tests need: PostgreSQL in a container.</summary>
public class Program
{
    public static void Main(string[] args)
    {
        var builder = DistributedApplication.CreateBuilder(args);

        builder.AddPostgres(Services.DatabaseServer).AddDatabase(Services.Database);

        builder.Build().Run();
    }
}
