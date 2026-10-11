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
    private readonly int _authRequestsPerMinute;

    /// <param name="connectionString">The test database.</param>
    /// <param name="authRequestsPerMinute">
    /// Auth rate limit for this host. Tests that are not about rate limiting raise it, because the
    /// production default of ten per minute would make a suite that signs in repeatedly fail for
    /// the wrong reason; the rate-limit test creates its own host with a low value.
    /// </param>
    public ApiFactory(string connectionString, int authRequestsPerMinute = 10_000)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
        _authRequestsPerMinute = authRequestsPerMinute;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment(Environments.Development);
        builder.UseSetting("ConnectionStrings:Default", _connectionString);

        // The fixture owns the schema; the API must not migrate it from under the tests.
        builder.UseSetting("Database:ApplyMigrationsOnStartup", "false");
        builder.UseSetting("Database:MaxRetryCount", "0");
        builder.UseSetting("Database:SeedDevelopmentData", "false");
        builder.UseSetting(
            "Api:RateLimit:AuthRequestsPerMinute",
            _authRequestsPerMinute.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
}
