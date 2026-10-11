using Microsoft.EntityFrameworkCore;
using RetailFlow.Application.Platform.Abstractions;
using RetailFlow.Infrastructure.Platform.Persistence;

namespace RetailFlow.Infrastructure.Platform.Services;

/// <summary>
/// Transaction boundary over the EF context. The operation runs inside the provider's execution
/// strategy, so a deadlock victim or a transient connection fault retries the whole transaction
/// instead of leaving half a sale behind (ADR-0003, ADR-0008).
/// </summary>
internal sealed class UnitOfWork : IUnitOfWork
{
    private readonly RetailFlowDbContext _context;

    public UnitOfWork(RetailFlowDbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
    }

    public async Task<TResult> ExecuteInTransactionAsync<TResult>(
        Func<CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        // Already inside a transaction (a composite operation, or a test fixture that wraps each
        // test): join it rather than nesting, which SQL Server does not support meaningfully.
        if (_context.Database.CurrentTransaction is not null)
        {
            return await operation(cancellationToken).ConfigureAwait(false);
        }

        var strategy = _context.Database.CreateExecutionStrategy();
        var attempt = 0;

        return await strategy.ExecuteAsync(
            async ct =>
            {
                // A retry would otherwise re-run the operation against entities the failed attempt
                // already tracked. Clearing makes every attempt start from the database.
                if (attempt++ > 0)
                {
                    _context.ChangeTracker.Clear();
                }

                await using var transaction = await _context.Database
                    .BeginTransactionAsync(ct)
                    .ConfigureAwait(false);

                var result = await operation(ct).ConfigureAwait(false);

                // The operation usually saves its own work; this catches anything still tracked so
                // nothing is silently dropped at commit.
                await _context.SaveChangesAsync(ct).ConfigureAwait(false);
                await transaction.CommitAsync(ct).ConfigureAwait(false);

                return result;
            },
            cancellationToken).ConfigureAwait(false);
    }

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
        _context.SaveChangesAsync(cancellationToken);
}
