using Infrastructure.Data;
using Scalar.AspNetCore;
using Web.Cli;
using Web.Hosting;

if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HOME")))
    Environment.SetEnvironmentVariable("HOME", Environment.GetEnvironmentVariable("PENRILL_DATA_DIR") ?? Path.GetTempPath());

if (CommandLine.IsCommand(args)) return await CommandLine.Run(args);

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
builder.AddApplicationServices();
builder.AddInfrastructureServices();
builder.AddWebServices();
builder.AddPenrillServer();

var app = builder.Build();
await app.InitialiseDatabaseAsync();
app.UsePenrillApiPipeline();
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}
app.MapPenrillServer();
await app.RunAsync();
return 0;

public partial class Program;
