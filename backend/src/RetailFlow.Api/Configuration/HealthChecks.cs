using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using RetailFlow.Infrastructure.Platform.Persistence;
using RetailFlow.Infrastructure.Platform.Persistence.Records;

namespace RetailFlow.Api.Configuration;

/// <summary>
/// Readiness check: can this instance actually serve traffic? It answers three questions a load
/// balancer cannot guess — is the database reachable, is the schema at the version this binary
/// expects, and is the outbox draining (NFR-07, docs/03-architecture.md §10).
/// </summary>
public sealed class DatabaseReadinessCheck : IHealthCheck
{
    /// <summary>
    /// Backlog above this is reported as degraded rather than unhealthy: events are still being
    /// written and will be delivered, but someone should look.
    /// </summary>
    private const int OutboxBacklogWarningThreshold = 1_000;

    private readonly RetailFlowDbContext _context;

    public DatabaseReadinessCheck(RetailFlowDbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!await _context.Database.CanConnectAsync(cancellationToken).ConfigureAwait(false))
            {
                return HealthCheckResult.Unhealthy("The database is not reachable.");
            }

            var pending = await _context.Database
                .GetPendingMigrationsAsync(cancellationToken)
                .ConfigureAwait(false);

            var pendingList = pending.ToArray();
            if (pendingList.Length > 0)
            {
                // The binary expects a newer schema than the database has. Serving traffic now
                // would fail in unpredictable places, so the instance reports itself not ready.
                return HealthCheckResult.Unhealthy(
                    $"The database is missing {pendingList.Length} migration(s): {string.Join(", ", pendingList)}");
            }

            var backlog = await _context.OutboxMessages
                .CountAsync(
                    m => m.Status == OutboxMessageStatus.Pending || m.Status == OutboxMessageStatus.Failed,
                    cancellationToken)
                .ConfigureAwait(false);

            var deadLettered = await _context.OutboxMessages
                .CountAsync(m => m.Status == OutboxMessageStatus.DeadLettered, cancellationToken)
                .ConfigureAwait(false);

            var data = new Dictionary<string, object>
            {
                ["outboxBacklog"] = backlog,
                ["outboxDeadLettered"] = deadLettered,
            };

            if (backlog > OutboxBacklogWarningThreshold || deadLettered > 0)
            {
                return HealthCheckResult.Degraded(
                    $"Outbox backlog {backlog}, dead-lettered {deadLettered}.",
                    data: data);
            }

            return HealthCheckResult.Healthy("Database reachable, schema current, outbox draining.", data);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy("The readiness check failed.", ex);
        }
    }
}
