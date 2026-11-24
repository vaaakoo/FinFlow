using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;

namespace FinFlow.Observability;

public static class FinFlowTelemetry
{
    public const string Name = "FinFlow.Workflows";
    public static readonly ActivitySource Activities = new(Name);
    private static readonly Meter Meter = new(Name);
    public static readonly Counter<long> Postings = Meter.CreateCounter<long>("finflow.ledger.postings");
    public static readonly Counter<long> PublishedEvents = Meter.CreateCounter<long>("finflow.events.published");
    public static readonly Counter<long> CompletedPayments = Meter.CreateCounter<long>("finflow.payments.settled");

    public static void Configure(WebApplicationBuilder builder, string serviceName)
    {
        var endpoint = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
        builder.Logging.ClearProviders();
        if (!string.IsNullOrWhiteSpace(endpoint))
        {
            builder.Logging.AddOpenTelemetry(options =>
            {
                options.SetResourceBuilder(ResourceBuilder.CreateDefault().AddService(serviceName));
                options.IncludeScopes = true;
                options.AddOtlpExporter();
            });
            builder.Services.AddOpenTelemetry()
                .ConfigureResource(resource => resource.AddService(serviceName))
                .WithTracing(tracing => tracing.AddAspNetCoreInstrumentation(options =>
                    {
                        options.Filter = context => context.Request.Path != "/customers/me/notifications/live";
                    })
                    .AddHttpClientInstrumentation()
                    .AddSource(Name, "Npgsql")
                    .AddOtlpExporter())
                .WithMetrics(metrics => metrics.AddMeter(Name, "Npgsql", "System.Net.Http", "Microsoft.AspNetCore.Hosting")
                    .AddOtlpExporter());
        }

        builder.Services.AddSerilog((_, configuration) => configuration
            .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore.Database.Command", LogEventLevel.Warning)
            .MinimumLevel.Override("Yarp.ReverseProxy.Forwarder.HttpForwarder", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Service", serviceName)
            .WriteTo.Console(new RenderedCompactJsonFormatter()), writeToProviders: true);
    }

    public static Activity? Start(string operation, ActivityKind kind, string? parentId)
    {
        if (ActivityContext.TryParse(parentId, null, out var parent))
            return Activities.StartActivity(operation, kind, parent);
        return Activities.StartActivity(operation, kind);
    }

    public static void AddDatabaseReadiness<TDb>(IServiceCollection services) where TDb : DbContext =>
        services.AddHealthChecks().AddCheck<DatabaseHealth<TDb>>("database", tags: ["ready"]);

    public static void MapHealthEndpoints(WebApplication app)
    {
        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
        app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });
    }
}

internal sealed class DatabaseHealth<TDb> : IHealthCheck where TDb : DbContext
{
    private readonly IServiceScopeFactory scopes;

    public DatabaseHealth(IServiceScopeFactory scopes) => this.scopes = scopes;

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TDb>();
        return await db.Database.CanConnectAsync(cancellationToken)
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy("Database is unavailable.");
    }
}
