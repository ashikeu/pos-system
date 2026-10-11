using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;

namespace RetailFlow.IntegrationTests.Infrastructure;

/// <summary>
/// Hosts the real API in-process against the test database, so endpoint tests exercise the actual
/// middleware pipeline — correlation ids, ProblemDetails mapping, health checks — rather than a
/// stripped-down stand-in.
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Api.Program>
{
    private readonly string _connectionString;

    public ApiFactory(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment(Environments.Development);
        builder.UseSetting("ConnectionStrings:Default", _connectionString);

        // The fixture owns the schema; the API must not migrate it from under the tests.
        builder.UseSetting("Database:ApplyMigrationsOnStartup", "false");
        builder.UseSetting("Database:MaxRetryCount", "0");
    }
}
