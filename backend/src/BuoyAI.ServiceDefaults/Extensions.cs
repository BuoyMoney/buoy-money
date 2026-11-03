using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Npgsql;
using System.Text.Json;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// Adds common .NET Aspire services: service discovery, resilience, health checks, and OpenTelemetry.
/// </summary>
public static class Extensions
{
    public static IHostApplicationBuilder AddServiceDefaults(this IHostApplicationBuilder builder)
    {
        builder.ConfigureOpenTelemetry();

        builder.AddDefaultHealthChecks();

        builder.Services.AddServiceDiscovery();

        builder.Services.ConfigureHttpClientDefaults(http =>
        {
            // Turn on resilience by default
            http.AddStandardResilienceHandler();

            // Turn on service discovery by default
            http.AddServiceDiscovery();
        });

        return builder;
    }

    public static IHostApplicationBuilder ConfigureOpenTelemetry(this IHostApplicationBuilder builder)
    {
        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        // Check if OTLP endpoint is configured
        var otlpEndpoint = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");
        var otlpProtocol = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_PROTOCOL");
        var otlpHeaders = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_HEADERS");
        
        Console.WriteLine($"[OTEL] OTEL_EXPORTER_OTLP_ENDPOINT: {otlpEndpoint ?? "(not set)"}");
        Console.WriteLine($"[OTEL] OTEL_EXPORTER_OTLP_PROTOCOL: {otlpProtocol ?? "(not set)"}");
        Console.WriteLine($"[OTEL] OTEL_EXPORTER_OTLP_HEADERS: {(string.IsNullOrEmpty(otlpHeaders) ? "(not set)" : "(set, hidden)")}");

        if (!string.IsNullOrWhiteSpace(otlpEndpoint))
        {
            Console.WriteLine($"[OTEL] Configuring OTLP exporter to: {otlpEndpoint}");
            
            // Use UseOtlpExporter which automatically enables all signals and reads env vars
            // This is the recommended approach for Grafana Cloud / external OTLP backends
            builder.Services.AddOpenTelemetry()
                .WithMetrics(metrics =>
                {
                    metrics.AddAspNetCoreInstrumentation()
                        .AddHttpClientInstrumentation()
                        .AddRuntimeInstrumentation()
                        .AddMeter("BuoyAI"); // Custom BuoyAI business metrics
                })
                .WithTracing(tracing =>
                {
                    tracing.AddAspNetCoreInstrumentation(o => o.Filter = ctx => !CarriesSecretInPath(ctx.Request.Path.Value))
                        // A SimpleFIN setup token and a Cloud link code are secrets carried in the URL path;
                        // keep those calls (in and out) out of exported traces.
                        .AddHttpClientInstrumentation(o => o.FilterHttpRequestMessage = r =>
                            !CarriesSecretInPath(r.RequestUri?.AbsolutePath))
                        .AddNpgsql() // PostgreSQL/Npgsql database tracing
                        .AddSource("BuoyAI"); // Custom BuoyAI traces
                })
                .UseOtlpExporter();
        }
        else
        {
            Console.WriteLine("[OTEL] No OTLP endpoint configured, using default (no export)");
            
            // Configure without exporter for local development
            builder.Services.AddOpenTelemetry()
                .WithMetrics(metrics =>
                {
                    metrics.AddAspNetCoreInstrumentation()
                        .AddHttpClientInstrumentation()
                        .AddRuntimeInstrumentation()
                        .AddMeter("BuoyAI"); // Custom BuoyAI business metrics
                })
                .WithTracing(tracing =>
                {
                    tracing.AddAspNetCoreInstrumentation(o => o.Filter = ctx => !CarriesSecretInPath(ctx.Request.Path.Value))
                        // A SimpleFIN setup token and a Cloud link code are secrets carried in the URL path;
                        // keep those calls (in and out) out of exported traces.
                        .AddHttpClientInstrumentation(o => o.FilterHttpRequestMessage = r =>
                            !CarriesSecretInPath(r.RequestUri?.AbsolutePath))
                        .AddNpgsql() // PostgreSQL/Npgsql database tracing
                        .AddSource("BuoyAI"); // Custom BuoyAI traces
                });
        }

        return builder;
    }

    /// <summary>
    /// True for a path that carries a secret: a SimpleFIN claim URL (one-time setup token) or a Cloud link session
    /// with its code (<c>.../link-sessions/BCDF-GH23</c>, also <c>/approve</c> and <c>/deny</c>). Such requests are
    /// kept out of traces. The server's own <c>link-sessions/poll</c> carries its code in the body, not the path.
    /// </summary>
    public static bool CarriesSecretInPath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        if (path.Contains("/simplefin/claim", StringComparison.OrdinalIgnoreCase)) return true;
        var at = path.IndexOf("/link-sessions/", StringComparison.OrdinalIgnoreCase);
        if (at < 0) return false;
        var rest = path[(at + "/link-sessions/".Length)..];
        return rest.Length > 0 && !rest.Equals("poll", StringComparison.OrdinalIgnoreCase);
    }

    public static IHostApplicationBuilder AddDefaultHealthChecks(this IHostApplicationBuilder builder)
    {
        builder.Services.AddHealthChecks()
            // Add a default liveness check to ensure app is responsive
            .AddCheck("self", () => HealthCheckResult.Healthy(), ["live"]);

        return builder;
    }

    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        // Health checks - used by orchestrators like Kubernetes
        // All health checks must pass for app to be considered ready
        // Returns detailed JSON with individual check statuses
        app.MapHealthChecks("/health", new HealthCheckOptions
        {
            ResponseWriter = WriteHealthCheckResponse,
            AllowCachingResponses = false
        });

        // Liveness check - only checks if app is responsive (not all dependencies)
        // Used for container restart decisions
        app.MapHealthChecks("/alive", new HealthCheckOptions
        {
            Predicate = r => r.Tags.Contains("live"),
            ResponseWriter = WriteHealthCheckResponse
        });

        // Public liveness for EXTERNAL uptime monitors. The edge nginx blocks "/health"
        // from the internet (it leaks dependency status) and proxies "/api/*" through to
        // the backend, so a health route under "/api" is the one an external monitor can
        // reach. Liveness-only (no dependency details) — safe to expose publicly.
        app.MapHealthChecks("/api/alive", new HealthCheckOptions
        {
            Predicate = r => r.Tags.Contains("live"),
            ResponseWriter = WriteHealthCheckResponse
        });

        // Database-only health check - for monitoring database connectivity
        app.MapHealthChecks("/health/db", new HealthCheckOptions
        {
            Predicate = r => r.Tags.Contains("db"),
            ResponseWriter = WriteHealthCheckResponse
        });

        return app;
    }
    
    /// <summary>
    /// Writes a detailed JSON response for health check endpoints
    /// </summary>
    private static async Task WriteHealthCheckResponse(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";
        
        var response = new
        {
            status = report.Status.ToString(),
            totalDuration = report.TotalDuration.TotalMilliseconds.ToString("F2") + "ms",
            checks = report.Entries.Select(e => new
            {
                name = e.Key,
                status = e.Value.Status.ToString(),
                description = e.Value.Description,
                duration = e.Value.Duration.TotalMilliseconds.ToString("F2") + "ms",
                exception = e.Value.Exception?.Message,
                tags = e.Value.Tags
            })
        };
        
        var json = JsonSerializer.Serialize(response, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        });
        
        await context.Response.WriteAsync(json);
    }
}
