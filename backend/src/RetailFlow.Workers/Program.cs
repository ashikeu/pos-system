using System.Globalization;
using Serilog;

namespace RetailFlow.Workers;

/// <summary>
/// Background host for the outbox dispatcher, provider status queries, alerting, retention and
/// reconciliation jobs. Phase 1 establishes the host; the first hosted service (the outbox
/// dispatcher, ADR-0011) is registered in Phase 2 — the host intentionally runs idle until then.
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
