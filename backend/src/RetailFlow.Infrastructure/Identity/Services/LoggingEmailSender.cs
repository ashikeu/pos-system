using Microsoft.Extensions.Logging;
using RetailFlow.Application.Identity.Abstractions;

namespace RetailFlow.Infrastructure.Identity.Services;

/// <summary>
/// Records the mail that would have been sent, for development and for environments where no
/// provider is configured (<c>Email:Provider = None</c>).
/// </summary>
/// <remarks>
/// This is an adapter for a missing prerequisite — SMTP credentials — not a stub standing in for
/// business logic: token creation, expiry and single use all happen for real in the handlers. The
/// body is logged at debug level because it contains a reset code; the outstanding work is a real
/// SMTP adapter, tracked in tasks.md.
/// </remarks>
internal sealed class LoggingEmailSender : IEmailSender
{
    private readonly ILogger<LoggingEmailSender> _logger;

    public LoggingEmailSender(ILogger<LoggingEmailSender> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    public Task SendAsync(
        string toAddress,
        string subject,
        string body,
        CancellationToken cancellationToken = default)
    {
        EmailLog.NotConfigured(_logger, toAddress, subject);
        EmailLog.Body(_logger, body);
        return Task.CompletedTask;
    }
}

internal static partial class EmailLog
{
    [LoggerMessage(
        EventId = 6100,
        Level = LogLevel.Warning,
        Message = "No email provider is configured; the message to {ToAddress} with subject '{Subject}' was not sent")]
    public static partial void NotConfigured(ILogger logger, string toAddress, string subject);

    [LoggerMessage(
        EventId = 6101,
        Level = LogLevel.Debug,
        Message = "Email body that would have been sent:\n{Body}")]
    public static partial void Body(ILogger logger, string body);
}
