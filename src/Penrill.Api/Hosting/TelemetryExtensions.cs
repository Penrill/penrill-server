using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Penrill.Core.Telemetry;

namespace Penrill.Api.Hosting;

public static class TelemetryExtensions
{
    public static IHostApplicationBuilder AddPenrillTelemetry(this IHostApplicationBuilder builder, string service)
    {
        builder.Services.Configure<OpenTelemetryLoggerOptions>(options =>
        {
            options.IncludeFormattedMessage = true;
            options.IncludeScopes = true;
        });
        var telemetry = builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(service, serviceVersion: typeof(TelemetryExtensions).Assembly.GetName().Version?.ToString(3)))
            .WithLogging()
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddMeter("System.Runtime")
                .AddMeter(PenrillTelemetry.Name))
            .WithTracing(tracing => tracing
                .AddAspNetCoreInstrumentation(options => options.Filter = context => !context.Request.Path.StartsWithSegments("/health"))
                .AddSource(PenrillTelemetry.Name));
        if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"])) telemetry.UseOtlpExporter();
        return builder;
    }
}
