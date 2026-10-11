using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RetailFlow.Application.Platform.Abstractions;
using RetailFlow.Infrastructure.Platform.Persistence;
using RetailFlow.Infrastructure.Platform.Persistence.Interceptors;
using RetailFlow.Infrastructure.Platform.Persistence.SqlServer;
using RetailFlow.Infrastructure.Platform.Services;

namespace RetailFlow.Infrastructure;

/// <summary>Database settings, bound from the <c>Database</c> configuration section (ADR-0023).</summary>
public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    /// <summary>Currently only <c>SqlServer</c>; the value exists so adding a provider is explicit.</summary>
    public string Provider { get; init; } = "SqlServer";

    public int CommandTimeoutSeconds { get; init; } = 30;

    /// <summary>Retries for transient faults, applied by the EF execution strategy.</summary>
    public int MaxRetryCount { get; init; } = 3;

    /// <summary>
    /// Development convenience only. Production applies migrations through a reviewed script in a
    /// controlled step, never on start-up (see docs/03-architecture.md §10).
    /// </summary>
    public bool ApplyMigrationsOnStartup { get; init; }

    /// <summary>Creates the demo tenant on start-up. Development only.</summary>
    public bool SeedDevelopmentData { get; init; }
}

/// <summary>Composition of the infrastructure layer.</summary>
public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var databaseOptions = configuration.GetSection(DatabaseOptions.SectionName).Get<DatabaseOptions>()
            ?? new DatabaseOptions();
        services.AddSingleton(databaseOptions);

        var outboxOptions = configuration.GetSection(OutboxOptions.SectionName).Get<OutboxOptions>()
            ?? new OutboxOptions();
        services.AddSingleton(outboxOptions);

        var idempotencyRetentionDays = configuration.GetValue<int?>("Api:IdempotencyRetentionDays");
        if (idempotencyRetentionDays is > 0)
        {
            services.AddSingleton(new IdempotencyOptions
            {
                Retention = TimeSpan.FromDays(idempotencyRetentionDays.Value),
            });
        }

        var connectionString = configuration.GetConnectionString("Default")
            ?? throw new InvalidOperationException(
                "ConnectionStrings:Default is not configured. Set it in appsettings.json (ADR-0023).");

        services.AddAmbientContexts();
        services.AddPlatformServices();
        services.AddPersistence(connectionString, databaseOptions);

        return services;
    }

    /// <summary>
    /// Registers the per-request scopes. Each is one scoped instance exposed through both its
    /// read-only and mutable interfaces, so handlers can only read what middleware has set.
    /// </summary>
    public static IServiceCollection AddAmbientContexts(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddScoped<AmbientTenantContext>();
        services.TryAddScoped<IMutableTenantContext>(sp => sp.GetRequiredService<AmbientTenantContext>());
        services.TryAddScoped<ITenantContext>(sp => sp.GetRequiredService<AmbientTenantContext>());

        services.TryAddScoped<AmbientCurrentUser>();
        services.TryAddScoped<IMutableCurrentUser>(sp => sp.GetRequiredService<AmbientCurrentUser>());
        services.TryAddScoped<ICurrentUser>(sp => sp.GetRequiredService<AmbientCurrentUser>());

        services.TryAddScoped<AmbientCorrelationContext>();
        services.TryAddScoped<IMutableCorrelationContext>(sp => sp.GetRequiredService<AmbientCorrelationContext>());
        services.TryAddScoped<ICorrelationContext>(sp => sp.GetRequiredService<AmbientCorrelationContext>());

        services.TryAddScoped<AmbientRequestMetadata>();
        services.TryAddScoped<IMutableRequestMetadata>(sp => sp.GetRequiredService<AmbientRequestMetadata>());
        services.TryAddScoped<IRequestMetadata>(sp => sp.GetRequiredService<AmbientRequestMetadata>());

        return services;
    }

    private static IServiceCollection AddPlatformServices(this IServiceCollection services)
    {
        services.TryAddSingleton<IClock, SystemClock>();
        services.TryAddSingleton<IGuidGenerator, UuidV7Generator>();

        services.AddMemoryCache();
        services.TryAddSingleton<ICacheService, MemoryCacheService>();
        services.TryAddSingleton<IDistributedLock, InProcessDistributedLock>();

        services.TryAddScoped<IUnitOfWork, UnitOfWork>();
        services.TryAddScoped<IIdempotencyStore, IdempotencyStore>();
        services.TryAddScoped<IAuditWriter, AuditWriter>();
        services.TryAddScoped<IEventPublisher, OutboxEventPublisher>();
        services.TryAddScoped<IRowLocker, SqlServerRowLocker>();
        services.TryAddScoped<OutboxDispatcher>();
        services.TryAddSingleton<IOutboxClaimStrategy, SqlServerOutboxClaimStrategy>();

        return services;
    }

    private static IServiceCollection AddPersistence(
        this IServiceCollection services,
        string connectionString,
        DatabaseOptions options)
    {
        if (!string.Equals(options.Provider, "SqlServer", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException(
                $"Database provider '{options.Provider}' is not supported yet. Only SqlServer is implemented (ADR-0003).");
        }

        services.TryAddSingleton<IChangeSequenceAllocator, SqlServerChangeSequenceAllocator>();
        services.AddScoped<TenantAuditInterceptor>();
        services.AddScoped<DevDataSeeder>();

        services.AddDbContext<RetailFlowDbContext>((provider, builder) =>
        {
            builder.UseSqlServer(connectionString, sql =>
            {
                sql.CommandTimeout(options.CommandTimeoutSeconds);
                sql.EnableRetryOnFailure(
                    options.MaxRetryCount,
                    TimeSpan.FromSeconds(10),
                    errorNumbersToAdd: null);
                sql.MigrationsHistoryTable("__EFMigrationsHistory");
            });

            builder.AddInterceptors(provider.GetRequiredService<TenantAuditInterceptor>());

            // No lazy-loading proxies are registered on purpose: an accidental N+1 inside a report
            // that runs per sale is a production incident, and explicit projections keep the cost
            // visible (NFR-08).
        });

        return services;
    }
}
