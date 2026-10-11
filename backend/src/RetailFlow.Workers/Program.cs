using System.Globalization;
using RetailFlow.Application;
using RetailFlow.Infrastructure;
using RetailFlow.Workers.Jobs;
using Serilog;

namespace RetailFlow.Workers;

/// <summary>
/// Background host. Phase 2 runs the outbox dispatcher; provider status queries, alerting,
/// retention and reconciliation jobs join it in later phases (see ../../../tasks.md).
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Console(formatProvider: CultureInfo.InvariantCulture)
            .CreateBootstrapLogger();

        try
        {
            var builder = Host.CreateApplicationBuilder(args);

            builder.Services.AddSerilog((services, configuration) => configuration
                .ReadFrom.Configuration(builder.Configuration)
                .ReadFrom.Services(services)
                .Enrich.FromLogContext()
                .Enrich.WithProperty("Application", "RetailFlow.Workers")
                .WriteTo.Console(formatProvider: CultureInfo.InvariantCulture));

            builder.Services
                .AddApplication()
                .AddInfrastructure(builder.Configuration);

            builder.Services.AddHostedService<OutboxDispatcherWorker>();

            var host = builder.Build();
            host.Run();
            return 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Fatal(ex, "RetailFlow workers terminated unexpectedly during start-up");
            return 1;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }
}
