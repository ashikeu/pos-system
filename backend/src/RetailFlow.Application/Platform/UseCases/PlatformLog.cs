using Microsoft.Extensions.Logging;

namespace RetailFlow.Application.Platform.UseCases;

/// <summary>
/// Source-generated log messages for the use-case pipeline. Every business operation passes
/// through here, so the allocation-free delegates are worth the extra declaration.
/// </summary>
public static partial class PlatformLog
{
    [LoggerMessage(
        EventId = 1000,
        Level = LogLevel.Information,
        Message = "{Operation} succeeded in {ElapsedMs} ms")]
    public static partial void OperationSucceeded(ILogger logger, string operation, long elapsedMs);

    [LoggerMessage(
        EventId = 1001,
        Level = LogLevel.Warning,
        Message = "{Operation} was rejected after {ElapsedMs} ms: {ErrorCode} {ErrorMessage}")]
    public static partial void OperationRejected(
        ILogger logger,
        string operation,
        long elapsedMs,
        string errorCode,
        string errorMessage);

    [LoggerMessage(
        EventId = 1002,
        Level = LogLevel.Information,
        Message = "{Operation} was cancelled after {ElapsedMs} ms")]
    public static partial void OperationCancelled(ILogger logger, string operation, long elapsedMs);

    [LoggerMessage(
        EventId = 1003,
        Level = LogLevel.Error,
        Message = "{Operation} failed unexpectedly after {ElapsedMs} ms")]
    public static partial void OperationFailed(ILogger logger, Exception exception, string operation, long elapsedMs);

    [LoggerMessage(
        EventId = 1010,
        Level = LogLevel.Information,
        Message = "Replaying stored response for idempotency key {IdempotencyKey} on {Operation}")]
    public static partial void IdempotencyReplay(ILogger logger, string idempotencyKey, string operation);

    [LoggerMessage(
        EventId = 1011,
        Level = LogLevel.Warning,
        Message = "Idempotency key {IdempotencyKey} on {Operation} is still in progress")]
    public static partial void IdempotencyInProgress(ILogger logger, string idempotencyKey, string operation);

    [LoggerMessage(
        EventId = 1012,
        Level = LogLevel.Warning,
        Message = "Idempotency key {IdempotencyKey} on {Operation} was reused with a different request body")]
    public static partial void IdempotencyKeyReuse(ILogger logger, string idempotencyKey, string operation);

    [LoggerMessage(
        EventId = 1013,
        Level = LogLevel.Error,
        Message = "Stored idempotent response for key {IdempotencyKey} on {Operation} could not be deserialized")]
    public static partial void IdempotencyReplayUnreadable(ILogger logger, string idempotencyKey, string operation);
}
