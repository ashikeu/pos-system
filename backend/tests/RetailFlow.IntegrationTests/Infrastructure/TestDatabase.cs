using System.Globalization;
using Microsoft.Data.SqlClient;

namespace RetailFlow.IntegrationTests.Infrastructure;

/// <summary>
/// Chooses the SQL Server instance the integration tests run against (ADR-0020). Tests never use
/// the EF in-memory provider: transactions, row locks, unique indexes and decimal precision are
/// exactly what they verify, and in-memory models none of it.
/// </summary>
/// <remarks>
/// Resolution order:
/// <list type="number">
/// <item><c>RETAILFLOW_TEST_CONNECTION</c>, used by CI where a Testcontainers instance supplies it;</item>
/// <item>the local default instance (<c>localhost</c>, Windows authentication);</item>
/// <item>SQL Server LocalDB, for a developer machine without a full instance.</item>
/// </list>
/// Each run creates its own database and drops it afterwards, so a failed run never poisons the next.
/// </remarks>
internal static class TestDatabase
{
    public const string EnvironmentVariable = "RETAILFLOW_TEST_CONNECTION";

    private static readonly string[] CandidateServers =
    [
        "Server=localhost;Integrated Security=True;TrustServerCertificate=True;Encrypt=False;Connect Timeout=5",
        @"Server=(localdb)\MSSQLLocalDB;Integrated Security=True;TrustServerCertificate=True;Connect Timeout=15",
    ];

    /// <summary>Returns a connection string for a uniquely named database for this test run.</summary>
    public static async Task<string> CreateDatabaseAsync(CancellationToken cancellationToken = default)
    {
        var baseConnection = await ResolveServerAsync(cancellationToken).ConfigureAwait(false);
        var databaseName = "RetailFlow_Test_" + Guid.NewGuid().ToString("N")[..12];

        var masterBuilder = new SqlConnectionStringBuilder(baseConnection) { InitialCatalog = "master" };

        await using (var connection = new SqlConnection(masterBuilder.ConnectionString))
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE DATABASE [{databaseName}]";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        return new SqlConnectionStringBuilder(baseConnection)
        {
            InitialCatalog = databaseName,
            MultipleActiveResultSets = true,
        }.ConnectionString;
    }

    public static async Task DropDatabaseAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        var databaseName = builder.InitialCatalog;
        builder.InitialCatalog = "master";

        SqlConnection.ClearAllPools();

        await using var connection = new SqlConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        // Single-user mode first: a lingering pooled connection would otherwise block the drop and
        // leave test databases accumulating on the developer's instance.
        command.CommandText = string.Format(
            CultureInfo.InvariantCulture,
            "IF DB_ID('{0}') IS NOT NULL BEGIN ALTER DATABASE [{0}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{0}]; END",
            databaseName);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> ResolveServerAsync(CancellationToken cancellationToken)
    {
        var configured = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        var failures = new List<string>();

        foreach (var candidate in CandidateServers)
        {
            try
            {
                var builder = new SqlConnectionStringBuilder(candidate) { InitialCatalog = "master" };
                await using var connection = new SqlConnection(builder.ConnectionString);
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                return candidate;
            }
            catch (SqlException ex)
            {
                failures.Add($"{candidate} -> {ex.Message}");
            }
        }

        throw new InvalidOperationException(
            "No SQL Server instance available for integration tests. Set " + EnvironmentVariable +
            " or start a local instance. Attempts: " + string.Join(" | ", failures));
    }
}
