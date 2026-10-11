using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Asp.Versioning;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.OpenApi.Models;
using RetailFlow.Api.Configuration;
using RetailFlow.Api.Middleware;
using RetailFlow.Application;
using RetailFlow.Infrastructure;
using RetailFlow.Infrastructure.Platform.Persistence;
using Serilog;
using Serilog.Events;

namespace RetailFlow.Api;

/// <summary>
/// Composition root. Phase 2 wires logging, correlation, ProblemDetails, persistence, the use-case
/// pipeline, health endpoints and OpenAPI. Authentication and authorization arrive in Phase 3; until
/// then there are no business endpoints to protect (see ../../../tasks.md).
/// </summary>
[SuppressMessage("Design", "CA1052:Static holder types should be static",
    Justification = "WebApplicationFactory in integration tests needs a non-static entry point type.")]
public class Program
{
    public static int Main(string[] args)
    {
        // Bootstrap logger so start-up failures are still structured and visible.
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Console(formatProvider: CultureInfo.InvariantCulture)
            .CreateBootstrapLogger();

        try
        {
            var app = BuildApp(args);
            ApplyStartupMigrations(app);
            app.Run();
            return 0;
        }
        catch (HostAbortedException)
        {
            // Raised by the EF Core design-time tools, which build the service provider and then
            // abort the host on purpose. Not a failure.
            return 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Fatal(ex, "RetailFlow API terminated unexpectedly during start-up");
            return 1;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    internal static WebApplication BuildApp(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Host.UseSerilog((context, services, configuration) => configuration
            .ReadFrom.Configuration(context.Configuration)
            .ReadFrom.Services(services)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Application", "RetailFlow.Api")
            .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
            .WriteTo.Console(formatProvider: CultureInfo.InvariantCulture));

        builder.Services
            .AddApplication()
            .AddInfrastructure(builder.Configuration);

        builder.Services.AddControllers()
            .AddJsonOptions(options =>
            {
                options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
                options.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
                options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
            });

        // Model-binding failures must speak the same error language as business failures, so the
        // client has one shape to parse (ADR-0014).
        builder.Services.Configure<ApiBehaviorOptions>(options =>
        {
            options.InvalidModelStateResponseFactory = context =>
            {
                var fieldErrors = context.ModelState
                    .Where(kvp => kvp.Value?.Errors.Count > 0)
                    .ToDictionary(
                        kvp => kvp.Key,
                        kvp => kvp.Value!.Errors.Select(e => e.ErrorMessage).ToArray(),
                        StringComparer.Ordinal);

                var problem = ApiProblemDetails.From(
                    Domain.Platform.Error.Validation(
                        "request.malformed",
                        "The request could not be read.",
                        fieldErrors),
                    context.HttpContext);

                return new ObjectResult(problem)
                {
                    StatusCode = problem.Status,
                    ContentTypes = { "application/problem+json" },
                };
            };
        });

        builder.Services.AddApiVersioning(options =>
            {
                options.DefaultApiVersion = new ApiVersion(1, 0);
                options.AssumeDefaultVersionWhenUnspecified = true;
                options.ReportApiVersions = true;
            })
            .AddApiExplorer(options =>
            {
                options.GroupNameFormat = "'v'V";
                options.SubstituteApiVersionInUrl = true;
            });

        builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
        builder.Services.AddProblemDetails();

        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen(options => options.SwaggerDoc("v1", new OpenApiInfo
        {
            Title = "RetailFlow POS API",
            Version = "v1",
            Description = "Multi-tenant point of sale and retail management platform.",
        }));

        builder.Services.AddHealthChecks()
            .AddCheck<DatabaseReadinessCheck>(
                "database",
                HealthStatus.Unhealthy,
                tags: ["ready"]);

        var app = builder.Build();
        ConfigurePipeline(app);
        return app;
    }

    private static void ConfigurePipeline(WebApplication app)
    {
        app.UseSerilogRequestLogging();
        app.UseExceptionHandler();

        // Gives bare status codes (404 on an unknown route, 405 on a wrong verb) the same
        // problem+json shape as business failures, so a client has one error format to parse.
        app.UseStatusCodePages();
        app.UseMiddleware<CorrelationIdMiddleware>();

        if (app.Environment.IsDevelopment())
        {
            app.UseSwagger();
            app.UseSwaggerUI();
        }

        // Liveness answers "is this process up"; readiness answers "can it serve traffic". A load
        // balancer that conflates the two restarts a healthy instance whose database blipped.
        app.MapHealthChecks("/health/live", new HealthCheckOptions
        {
            Predicate = _ => false,
        });

        app.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains("ready"),
            ResponseWriter = HealthResponseWriter.WriteAsync,
        });

        app.MapControllers();
    }

    /// <summary>
    /// Applies migrations only when configuration explicitly allows it (development and tests).
    /// Shared environments get a reviewed script applied in a controlled step, so a deployment can
    /// never silently reshape a production database.
    /// </summary>
    private static void ApplyStartupMigrations(WebApplication app)
    {
        var options = app.Services.GetRequiredService<DatabaseOptions>();
        if (!options.ApplyMigrationsOnStartup)
        {
            return;
        }

        using var scope = app.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<RetailFlowDbContext>();
        context.Database.Migrate();

        if (options.SeedDevelopmentData)
        {
            scope.ServiceProvider.GetRequiredService<DevDataSeeder>()
                .SeedAsync()
                .GetAwaiter()
                .GetResult();
        }
    }

}

/// <summary>Writes the readiness result as JSON so operators see which dependency is unhappy.</summary>
internal static class HealthResponseWriter
{
    public static Task WriteAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";

        var payload = new
        {
            status = report.Status.ToString(),
            totalDurationMs = report.TotalDuration.TotalMilliseconds,
            checks = report.Entries.Select(entry => new
            {
                name = entry.Key,
                status = entry.Value.Status.ToString(),
                description = entry.Value.Description,
                durationMs = entry.Value.Duration.TotalMilliseconds,
                data = entry.Value.Data,
            }),
        };

        return context.Response.WriteAsJsonAsync(payload);
    }
}
