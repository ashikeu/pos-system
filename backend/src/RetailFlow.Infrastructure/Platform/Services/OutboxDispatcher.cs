using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RetailFlow.Application.Platform.Abstractions;
using RetailFlow.Infrastructure.Platform.Persistence;
using RetailFlow.Infrastructure.Platform.Persistence.Records;

namespace RetailFlow.Infrastructure.Platform.Services;

/// <summary>
/// Claims and delivers outbox messages (ADR-0011). Delivery is at-least-once: a crash between a
/// successful handler and the status update replays the message, which is why handlers must be
/// idempotent. The alternative — marking delivered first — would silently lose events, and a lost
/// stock or loyalty event is worse than a repeated one.
/// </summary>
public sealed class OutboxDispatcher
{
    private readonly RetailFlowDbContext _context;
    private readonly IOutboxClaimStrategy _claims;
    private readonly IEnumerable<IOutboxMessageHandler> _handlers;
    private readonly IMutableTenantContext _tenant;
    private readonly IClock _clock;
    private readonly OutboxOptions _options;
    private readonly ILogger<OutboxDispatcher> _logger;

    public OutboxDispatcher(
        RetailFlowDbContext context,
        IOutboxClaimStrategy claims,
        IEnumerable<IOutboxMessageHandler> handlers,
        IMutableTenantContext tenant,
        IClock clock,
        OutboxOptions options,
        ILogger<OutboxDispatcher> logger)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(claims);
        ArgumentNullException.ThrowIfNull(handlers);
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _context = context;
        _claims = claims;
        _handlers = handlers;
        _tenant = tenant;
        _clock = clock;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Processes one batch and returns how many messages were handled. Returning zero tells the
    /// worker to wait before polling again.
    /// </summary>
    public async Task<int> DispatchBatchAsync(string owner, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);

        var now = _clock.UtcNow;
        var leaseDuration = TimeSpan.FromSeconds(_options.LeaseSeconds);

        var claimedIds = await _claims
            .ClaimAsync(_context, owner, now, leaseDuration, _options.BatchSize, cancellationToken)
            .ConfigureAwait(false);

        if (claimedIds.Count == 0)
        {
            return 0;
        }

        var messages = await _context.OutboxMessages
            .Where(m => claimedIds.Contains(m.Id))
            .OrderBy(m => m.CreatedAtUtc)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var handled = 0;

        foreach (var message in messages)
        {
            // One message per transaction: a poison message must not roll back the ones that
            // succeeded alongside it.
            await DispatchOneAsync(message, cancellationToken).ConfigureAwait(false);
            handled++;
        }

        return handled;
    }

    private async Task DispatchOneAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        var handlers = _handlers
            .Where(h => string.Equals(h.MessageType, message.MessageType, StringComparison.Ordinal))
            .ToArray();

        try
        {
            if (handlers.Length == 0)
            {
                // No consumer yet. The message is still durable history, so it is marked delivered
                // rather than retried forever.
                OutboxLog.NoHandler(_logger, message.MessageType, message.Id);
            }
            else
            {
                if (message.OrganizationId is { } organizationId)
                {
                    _tenant.Set(new TenantScope(organizationId));
                }

                foreach (var handler in handlers)
                {
                    await handler.HandleAsync(message.PayloadJson, cancellationToken).ConfigureAwait(false);
                }
            }

            message.MarkProcessed(_clock.UtcNow);
            await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            OutboxLog.Delivered(_logger, message.MessageType, message.Id, handlers.Length);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            message.MarkFailed(
                _clock.UtcNow,
                ex.ToString(),
                _options.MaxAttempts,
                TimeSpan.FromSeconds(_options.BaseBackoffSeconds),
                TimeSpan.FromSeconds(_options.MaxBackoffSeconds),
                _options.JitterFactor);

            if (message.Status == OutboxMessageStatus.DeadLettered)
            {
                OutboxLog.DeadLettered(_logger, ex, message.MessageType, message.Id, message.AttemptCount);
            }
            else
            {
                OutboxLog.Retrying(_logger, ex, message.MessageType, message.Id, message.NextAttemptAtUtc);
            }

            await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _tenant.Clear();
        }
    }
}

/// <summary>
/// Claims a batch of due messages for one dispatcher. Separated because the claim has to be a
/// single atomic statement, which is provider-specific (ADR-0003).
/// </summary>
public interface IOutboxClaimStrategy
{
    Task<IReadOnlyList<Guid>> ClaimAsync(
        DbContext context,
        string owner,
        DateTimeOffset nowUtc,
        TimeSpan leaseDuration,
        int batchSize,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// SQL Server claim: one statement marks the batch as leased and returns the ids. <c>READPAST</c>
/// lets a second dispatcher skip rows already locked by the first instead of blocking behind them,
/// so two instances share the queue instead of serialising on it.
/// </summary>
internal sealed class SqlServerOutboxClaimStrategy : IOutboxClaimStrategy
{
    private const string ClaimSql = """
        UPDATE TOP (@batchSize) [OutboxMessage] WITH (READPAST, UPDLOCK, ROWLOCK)
        SET [Status] = 1,
            [LeaseOwner] = @owner,
            [LeaseExpiresAtUtc] = @leaseExpires
        OUTPUT inserted.[Id] AS [Value]
        WHERE ([Status] IN (0, 3) AND [NextAttemptAtUtc] <= @now)
           OR ([Status] = 1 AND [LeaseExpiresAtUtc] < @now)
        """;

    public async Task<IReadOnlyList<Guid>> ClaimAsync(
        DbContext context,
        string owner,
        DateTimeOffset nowUtc,
        TimeSpan leaseDuration,
        int batchSize,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);

        if (batchSize < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(batchSize), batchSize, "Batch size must be at least 1.");
        }

        return await context.Database
            .SqlQueryRaw<Guid>(
                ClaimSql,
                new SqlParameter("@batchSize", batchSize),
                new SqlParameter("@owner", owner),
                new SqlParameter("@leaseExpires", nowUtc.Add(leaseDuration)),
                new SqlParameter("@now", nowUtc))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}

/// <summary>Source-generated log messages for the dispatcher.</summary>
public static partial class OutboxLog
{
    [LoggerMessage(
        EventId = 3000,
        Level = LogLevel.Debug,
        Message = "Delivered outbox message {MessageType} {MessageId} to {HandlerCount} handler(s)")]
    public static partial void Delivered(ILogger logger, string messageType, Guid messageId, int handlerCount);

    [LoggerMessage(
        EventId = 3001,
        Level = LogLevel.Debug,
        Message = "No handler registered for outbox message {MessageType} {MessageId}; marking delivered")]
    public static partial void NoHandler(ILogger logger, string messageType, Guid messageId);

    [LoggerMessage(
        EventId = 3002,
        Level = LogLevel.Warning,
        Message = "Outbox message {MessageType} {MessageId} failed; next attempt at {NextAttemptAtUtc}")]
    public static partial void Retrying(
        ILogger logger,
        Exception exception,
        string messageType,
        Guid messageId,
        DateTimeOffset nextAttemptAtUtc);

    [LoggerMessage(
        EventId = 3003,
        Level = LogLevel.Error,
        Message = "Outbox message {MessageType} {MessageId} dead-lettered after {AttemptCount} attempts")]
    public static partial void DeadLettered(
        ILogger logger,
        Exception exception,
        string messageType,
        Guid messageId,
        int attemptCount);
}
