using Penrill.Api;
using Penrill.Api.Hosting;
using Penrill.Storage.Sqlite;

var builder = WebApplication.CreateBuilder(args);
builder.AddPenrillTelemetry("penrill-server");
builder.Services.AddSqliteStorage();
builder.AddPenrillApi();
if (builder.Configuration["urls"] is null) builder.WebHost.UseUrls("http://127.0.0.1:5081");

var app = builder.Build();
app.MapPenrillApi();
app.MapGet("/health", () => Results.Ok(new { service = "penrill-server" }));
app.Run();

public partial class Program;
