using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.OpenApi.Models;
using Serilog;
using Serilog.Events;

namespace RetailFlow.Api;

/// <summary>
/// Composition root. Phase 1 wires logging, controllers, OpenAPI and health endpoints only;
/// persistence, authentication, authorization, idempotency and the outbox arrive in Phase 2
/// (see ../../../tasks.md).
/// </summary>
[SuppressMessage("Design", "CA1052:Static holder types should be static",
    Justification = "WebApplicationFactory in integration tests needs a non-static entry point type.")]
public class Program
{
    public static int Main(string[] args)
    {
        // Bootstrap logger so failures during start-up are still structured and visible.
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Console(formatProvider: CultureInfo.InvariantCulture)
            .CreateBootstrapLogger();

        try
        {
            var app = BuildApp(args);
            app.Run();
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

    private static WebApplication BuildApp(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Host.UseSerilog((context, services, configuration) => configuration
            .ReadFrom.Configuration(context.Configuration)
            .ReadFrom.Services(services)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Application", "RetailFlow.Api")
            .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
            .WriteTo.Console(formatProvider: CultureInfo.InvariantCulture));

        builder.Services.AddControllers();
        builder.Services.AddProblemDetails();
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen(options => options.SwaggerDoc("v1", new OpenApiInfo
        {
            Title = "RetailFlow POS API",
            Version = "v1",
            Description = "Multi-tenant point of sale and retail management platform.",
        }));

        // Readiness gains real dependency checks (database, migrations, outbox backlog) in Phase 2.
        builder.Services.AddHealthChecks();

        var app = builder.Build();

        app.UseSerilogRequestLogging();

        if (app.Environment.IsDevelopment())
        {
            app.UseSwagger();
            app.UseSwaggerUI();
        }

        app.UseExceptionHandler();
        app.UseStatusCodePages();

        app.MapHealthChecks("/health/live");
        app.MapHealthChecks("/health/ready");
        app.MapControllers();

        return app;
    }
}
