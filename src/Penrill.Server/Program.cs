using Penrill.Api;
using Penrill.Api.Hosting;
using Penrill.Server.Cli;
using Penrill.Server.Hosting;
using Penrill.Storage.Sqlite;

if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HOME")))
    Environment.SetEnvironmentVariable("HOME", Environment.GetEnvironmentVariable("PENRILL_DATA_DIR") ?? Path.GetTempPath());

if (CommandLine.IsCommand(args)) return await CommandLine.Run(args);

var builder = WebApplication.CreateBuilder(args);
builder.AddPenrillTelemetry("penrill-server");
builder.Services.AddSqliteStorage();
builder.AddPenrillApi();
builder.AddPenrillServer();

var app = builder.Build();
app.MapPenrillApi();
app.MapPenrillServer();
await app.RunAsync();
return 0;

public partial class Program;
