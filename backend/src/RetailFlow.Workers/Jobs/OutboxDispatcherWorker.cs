using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RetailFlow.Application.Platform.Abstractions;
using RetailFlow.Infrastructure.Platform.Services;

namespace RetailFlow.Workers.Jobs;

/// <summary>
/// Drains the transactional outbox (ADR-0011). Each iteration takes its own scope, so a failure
/// cannot poison the next one, and the loop keeps running through database outages: a worker that
/// exits on error would silently stop every downstream projection and alert in the system.
/// </summary>
public sealed class OutboxDispatcherWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly OutboxOptions _options;
    private readonly ILogger<OutboxDispatcherWorker> _logger;
    private readonly string _owner;

    public OutboxDispatcherWorker(
        IServiceScopeFactory scopeFactory,
        OutboxOptions options,
        ILogger<OutboxDispatcherWorker> logger)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;

        // Identifies this instance in the lease, so an operator can see which host is stuck.
        _owner = $"{Environment.MachineName}:{Environment.ProcessId}";
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        WorkerLog.DispatcherStarted(_logger, _owner, _options.BatchSize, _options.PollIntervalSeconds);

        var idleDelay = TimeSpan.FromSeconds(Math.Max(1, _options.PollIntervalSeconds));

        while (!stoppingToken.IsCancellationRequested)
        {
            var handled = 0;

            try
            {
                using var scope = _scopeFactory.CreateScope();
                var dispatcher = scope.ServiceProvider.GetRequiredService<OutboxDispatcher>();
                handled = await dispatcher.DispatchBatchAsync(_owner, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                WorkerLog.DispatcherIterationFailed(_logger, ex);
            }

            // A full batch means there is probably more waiting, so poll again immediately.
            if (handled < _options.BatchSize)
            {
                try
                {
                    await Task.Delay(idleDelay, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        WorkerLog.DispatcherStopped(_logger, _owner);
    }
}

/// <summary>Source-generated log messages for the worker host.</summary>
public static partial class WorkerLog
{
    [LoggerMessage(
        EventId = 4000,
        Level = LogLevel.Information,
        Message = "Outbox dispatcher {Owner} started (batch {BatchSize}, poll {PollIntervalSeconds}s)")]
    public static partial void DispatcherStarted(
        ILogger logger,
        string owner,
        int batchSize,
        int pollIntervalSeconds);

    [LoggerMessage(
        EventId = 4001,
        Level = LogLevel.Error,
        Message = "Outbox dispatcher iteration failed; the loop continues")]
    public static partial void DispatcherIterationFailed(ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 4002,
        Level = LogLevel.Information,
        Message = "Outbox dispatcher {Owner} stopped")]
    public static partial void DispatcherStopped(ILogger logger, string owner);
}
