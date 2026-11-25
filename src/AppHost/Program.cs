using Shared;

// Local development: `aspire run` starts PostgreSQL in a container and Penrill Server against it,
// with open registration and the Aspire dashboard for logs, traces and metrics.
var builder = DistributedApplication.CreateBuilder(args);

var database = builder.AddPostgres(Services.DatabaseServer)
    .WithDataVolume()
    .AddDatabase(Services.Database);

builder.AddProject<Projects.Web>(Services.WebApi)
    .WithReference(database)
    .WaitFor(database)
    .WithEnvironment("PENRILL_OPEN_REGISTRATION", "true")
    .WithExternalHttpEndpoints()
    .WithAspNetCoreEnvironment()
    .WithUrlForEndpoint("http", url =>
    {
        url.DisplayText = "Scalar API Reference";
        url.Url = "/scalar";
    });

builder.Build().Run();
