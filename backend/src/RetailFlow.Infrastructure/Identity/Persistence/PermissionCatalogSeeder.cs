using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RetailFlow.Domain.Identity;
using RetailFlow.Infrastructure.Platform.Persistence;

namespace RetailFlow.Infrastructure.Identity.Persistence;

/// <summary>
/// Keeps the <c>Permission</c> table in step with the catalog in source (ADR-0013). Runs on
/// start-up: adding a permission is a code change, and the database has to know about it before a
/// role can reference it.
/// </summary>
/// <remarks>
/// Codes are never deleted here, even when they disappear from the catalog. A removed code may
/// still be referenced by a role in a tenant's database and by historical audit rows; the permission
/// resolver already ignores codes the application no longer knows, so a stale row is harmless while
/// a cascade of deletions would not be.
/// </remarks>
public sealed class PermissionCatalogSeeder
{
    private readonly RetailFlowDbContext _context;
    private readonly ILogger<PermissionCatalogSeeder> _logger;

    public PermissionCatalogSeeder(RetailFlowDbContext context, ILogger<PermissionCatalogSeeder> logger)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(logger);

        _context = context;
        _logger = logger;
    }

    public async Task SyncAsync(CancellationToken cancellationToken = default)
    {
        var existing = await _context.Permissions
            .ToDictionaryAsync(p => p.Code, StringComparer.Ordinal, cancellationToken)
            .ConfigureAwait(false);

        var nextId = existing.Count == 0 ? 1 : existing.Values.Max(p => p.Id) + 1;
        var added = 0;
        var updated = 0;

        foreach (var definition in Permissions.All)
        {
            if (existing.TryGetValue(definition.Code, out var row))
            {
                if (!string.Equals(row.Module, definition.Module, StringComparison.Ordinal)
                    || !string.Equals(row.Description, definition.Description, StringComparison.Ordinal))
                {
                    row.Describe(definition.Module, definition.Description);
                    updated++;
                }

                continue;
            }

            _context.Permissions.Add(Permission.Create(
                nextId++,
                definition.Code,
                definition.Module,
                definition.Description));
            added++;
        }

        if (added > 0 || updated > 0)
        {
            await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            PermissionSeedLog.Synced(_logger, added, updated, Permissions.All.Count);
        }
    }
}

internal static partial class PermissionSeedLog
{
    [LoggerMessage(
        EventId = 6200,
        Level = LogLevel.Information,
        Message = "Permission catalog synced: {Added} added, {Updated} updated, {Total} total")]
    public static partial void Synced(ILogger logger, int added, int updated, int total);
}
