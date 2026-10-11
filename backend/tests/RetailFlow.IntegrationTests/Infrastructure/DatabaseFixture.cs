using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RetailFlow.Application;
using RetailFlow.Application.Platform.Abstractions;
using RetailFlow.Domain.Tenancy;
using RetailFlow.Infrastructure;
using RetailFlow.Infrastructure.Platform.Persistence;
using Xunit;

namespace RetailFlow.IntegrationTests.Infrastructure;

/// <summary>
/// Creates one real SQL Server database for the test run, applies every migration to it, and hands
/// out scopes with a chosen tenant. Shared by the whole collection because creating a database per
/// test would make the suite too slow to run on every change; tests stay independent by using
/// their own organization ids and keys.
/// </summary>
public sealed class DatabaseFixture : IAsyncLifetime
{
    private ServiceProvider? _provider;

    public string ConnectionString { get; private set; } = string.Empty;

    public IServiceProvider Services => _provider
        ?? throw new InvalidOperationException("The fixture has not been initialised.");

    public async Task InitializeAsync()
    {
        ConnectionString = await TestDatabase.CreateDatabaseAsync().ConfigureAwait(false);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = ConnectionString,
                ["Database:Provider"] = "SqlServer",
                ["Database:CommandTimeoutSeconds"] = "30",
                ["Database:MaxRetryCount"] = "0",
                ["Outbox:BatchSize"] = "10",
                ["Outbox:MaxAttempts"] = "3",
                ["Outbox:BaseBackoffSeconds"] = "5",
                ["Outbox:MaxBackoffSeconds"] = "60",
                ["Outbox:JitterFactor"] = "0",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
        services.AddApplication();
        services.AddInfrastructure(configuration);
        TestUseCases.Register(services);

        _provider = services.BuildServiceProvider(validateScopes: true);

        // Proves the migration chain applies to an empty database on every run (Phase 2 exit
        // criterion), not just that someone once ran it locally.
        await using var scope = _provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<RetailFlowDbContext>();
        await context.Database.MigrateAsync().ConfigureAwait(false);
    }

    public async Task DisposeAsync()
    {
        if (_provider is not null)
        {
            await _provider.DisposeAsync().ConfigureAwait(false);
        }

        if (!string.IsNullOrEmpty(ConnectionString))
        {
            await TestDatabase.DropDatabaseAsync(ConnectionString).ConfigureAwait(false);
        }
    }

    /// <summary>Opens a scope acting as the given tenant and user.</summary>
    public AsyncServiceScope CreateScope(Guid? organizationId = null, Guid? userId = null, Guid? branchId = null)
    {
        var scope = Services.CreateAsyncScope();

        if (organizationId is { } tenant)
        {
            scope.ServiceProvider.GetRequiredService<IMutableTenantContext>()
                .Set(new TenantScope(tenant, branchId));
        }

        scope.ServiceProvider.GetRequiredService<IMutableCurrentUser>()
            .Set(new UserScope(userId, "integration-test"));

        return scope;
    }

    /// <summary>
    /// Creates an organization and its change-sequence counter directly, bypassing the tenant
    /// filter. Phase 4 replaces this with the real provisioning use case.
    /// </summary>
    public async Task<Organization> CreateOrganizationAsync(string code, string? name = null)
    {
        await using var scope = Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<RetailFlowDbContext>();
        var guids = scope.ServiceProvider.GetRequiredService<IGuidGenerator>();

        var organizationId = guids.NewId();
        var organization = Organization.Create(organizationId, code, name ?? code + " Shop");

        // The organization row is written under its own tenant context so the interceptor can
        // sequence it like any other record.
        scope.ServiceProvider.GetRequiredService<IMutableTenantContext>()
            .Set(new TenantScope(organizationId));

        context.Organizations.Add(organization);
        await context.SaveChangesAsync().ConfigureAwait(false);

        return organization;
    }
}

/// <summary>
/// All database tests share one collection so they share one database and run sequentially, which
/// keeps row-lock behaviour predictable.
/// </summary>
[CollectionDefinition(Name)]
public sealed class DatabaseCollection : ICollectionFixture<DatabaseFixture>
{
    public const string Name = "database";
}
