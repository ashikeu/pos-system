using System.Diagnostics;
using Microsoft.Extensions.Logging;
using RetailFlow.Application.Platform.Abstractions;
using RetailFlow.Domain.Platform;

namespace RetailFlow.Application.Platform.UseCases;

/// <summary>
/// Outermost decorator: records that an operation ran, how long it took and how it ended, with the
/// tenant, user and correlation id attached. Business failures are logged at warning level and
/// unexpected exceptions at error level, so an operations dashboard can tell a rejected sale from
/// a broken one.
/// </summary>
public sealed class LoggingUseCase<TRequest, TResponse> : IUseCase<TRequest, TResponse>
    where TRequest : notnull
{
    private readonly IUseCase<TRequest, TResponse> _inner;
    private readonly ILogger<LoggingUseCase<TRequest, TResponse>> _logger;
    private readonly ITenantContext _tenant;
    private readonly ICurrentUser _currentUser;
    private readonly ICorrelationContext _correlation;

    public LoggingUseCase(
        IUseCase<TRequest, TResponse> inner,
        ILogger<LoggingUseCase<TRequest, TResponse>> logger,
        ITenantContext tenant,
        ICurrentUser currentUser,
        ICorrelationContext correlation)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(currentUser);
        ArgumentNullException.ThrowIfNull(correlation);

        _inner = inner;
        _logger = logger;
        _tenant = tenant;
        _currentUser = currentUser;
        _correlation = correlation;
    }

    public async Task<Result<TResponse>> HandleAsync(
        TRequest request,
        CancellationToken cancellationToken = default)
    {
        var operation = typeof(TRequest).Name;
        using var scope = _logger.BeginScope(new Dictionary<string, object?>
        {
            ["Operation"] = operation,
            ["OrganizationId"] = _tenant.OrganizationIdOrNull,
            ["BranchId"] = _tenant.BranchId,
            ["TerminalId"] = _tenant.TerminalId,
            ["UserId"] = _currentUser.UserId,
            ["CorrelationId"] = _correlation.CorrelationId,
        });

        var stopwatch = Stopwatch.StartNew();

        try
        {
            var result = await _inner.HandleAsync(request, cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();

            if (result.IsSuccess)
            {
                PlatformLog.OperationSucceeded(_logger, operation, stopwatch.ElapsedMilliseconds);
            }
            else
            {
                PlatformLog.OperationRejected(
                    _logger,
                    operation,
                    stopwatch.ElapsedMilliseconds,
                    result.Error.Code,
                    result.Error.Message);
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            PlatformLog.OperationCancelled(_logger, operation, stopwatch.ElapsedMilliseconds);
            throw;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            PlatformLog.OperationFailed(_logger, ex, operation, stopwatch.ElapsedMilliseconds);
            throw;
        }
    }
}
