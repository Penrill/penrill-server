using Penrill.Shared;

// Local development: `aspire run` starts the server with open registration and a data folder under the app,
// and opens the Aspire dashboard with its logs, traces and metrics.
var builder = DistributedApplication.CreateBuilder(args);

builder.AddProject<Projects.Penrill_Server>(Services.WebApi)
    .WithEnvironment("PENRILL_OPEN_REGISTRATION", "true")
    .WithExternalHttpEndpoints()
    .WithAspNetCoreEnvironment()
    .WithUrlForEndpoint("http", url =>
    {
        url.DisplayText = "Scalar API Reference";
        url.Url = "/scalar";
    });

builder.Build().Run();
