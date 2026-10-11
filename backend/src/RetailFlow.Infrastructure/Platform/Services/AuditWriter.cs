using RetailFlow.Application.Platform.Abstractions;
using RetailFlow.Application.Platform.UseCases;
using RetailFlow.Domain.Platform;
using RetailFlow.Infrastructure.Platform.Persistence;
using RetailFlow.Infrastructure.Platform.Persistence.Records;

namespace RetailFlow.Infrastructure.Platform.Services;

/// <summary>
/// Writes audit rows through the same context as the business change, so the two commit together
/// (FR-SEC-11). An audit trail that can be written without the change, or vice versa, is worse
/// than none: it would be trusted while being wrong.
/// </summary>
internal sealed class AuditWriter : IAuditWriter
{
    private readonly RetailFlowDbContext _context;
    private readonly ITenantContext _tenant;
    private readonly ICurrentUser _currentUser;
    private readonly ICorrelationContext _correlation;
    private readonly IClock _clock;
    private readonly IGuidGenerator _guids;
    private readonly IRequestMetadata _requestMetadata;

    public AuditWriter(
        RetailFlowDbContext context,
        ITenantContext tenant,
        ICurrentUser currentUser,
        ICorrelationContext correlation,
        IClock clock,
        IGuidGenerator guids,
        IRequestMetadata requestMetadata)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(currentUser);
        ArgumentNullException.ThrowIfNull(correlation);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(guids);
        ArgumentNullException.ThrowIfNull(requestMetadata);

        _context = context;
        _tenant = tenant;
        _currentUser = currentUser;
        _correlation = correlation;
        _clock = clock;
        _guids = guids;
        _requestMetadata = requestMetadata;
    }

    public async Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var now = _clock.UtcNow;

        var record = AuditLogEntry.Create(
            _guids.NewId(now),
            _tenant.OrganizationIdOrNull,
            _currentUser.UserId,
            _currentUser.UserName,
            _tenant.BranchId,
            _tenant.TerminalId,
            entry.Action,
            entry.EntityType,
            entry.EntityId,
            entry.Before is null ? null : PlatformJson.Serialize(entry.Before),
            entry.After is null ? null : PlatformJson.Serialize(entry.After),
            entry.Metadata is null ? null : PlatformJson.Serialize(entry.Metadata),
            _correlation.CorrelationId,
            _requestMetadata.IpAddress,
            _requestMetadata.UserAgent,
            now);

        await _context.AuditLogs.AddAsync(record, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// Transport details of the current call, recorded on audit rows and login history. Supplied by
/// the API layer; background jobs leave it empty.
/// </summary>
public interface IRequestMetadata
{
    string? IpAddress { get; }

    string? UserAgent { get; }
}

/// <summary>Write side of <see cref="IRequestMetadata"/>, set by API middleware.</summary>
public interface IMutableRequestMetadata : IRequestMetadata
{
    void Set(string? ipAddress, string? userAgent);
}

/// <summary>Per-request transport details.</summary>
internal sealed class AmbientRequestMetadata : IMutableRequestMetadata
{
    public string? IpAddress { get; private set; }

    public string? UserAgent { get; private set; }

    public void Set(string? ipAddress, string? userAgent)
    {
        IpAddress = Truncate(ipAddress, 64);
        UserAgent = Truncate(userAgent, 512);
    }

    private static string? Truncate(string? value, int maxLength) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : value.Length > maxLength ? value[..maxLength] : value;
}

/// <summary>
/// Publishes domain events by appending them to the outbox in the current transaction (ADR-0011).
/// Used by handlers that raise an event without going through an aggregate.
/// </summary>
internal sealed class OutboxEventPublisher : IEventPublisher
{
    private readonly RetailFlowDbContext _context;
    private readonly ITenantContext _tenant;
    private readonly ICorrelationContext _correlation;
    private readonly IClock _clock;
    private readonly IGuidGenerator _guids;

    public OutboxEventPublisher(
        RetailFlowDbContext context,
        ITenantContext tenant,
        ICorrelationContext correlation,
        IClock clock,
        IGuidGenerator guids)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(correlation);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(guids);

        _context = context;
        _tenant = tenant;
        _correlation = correlation;
        _clock = clock;
        _guids = guids;
    }

    public async Task PublishAsync(IDomainEvent domainEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        var now = _clock.UtcNow;

        var message = OutboxMessage.Create(
            domainEvent.EventId == Guid.Empty ? _guids.NewId(now) : domainEvent.EventId,
            _tenant.OrganizationIdOrNull,
            domainEvent.EventType,
            PlatformJson.Serialize((object)domainEvent),
            now,
            _correlation.CorrelationId);

        await _context.OutboxMessages.AddAsync(message, cancellationToken).ConfigureAwait(false);
    }

    public async Task PublishAsync(
        IEnumerable<IDomainEvent> domainEvents,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(domainEvents);

        foreach (var domainEvent in domainEvents)
        {
            await PublishAsync(domainEvent, cancellationToken).ConfigureAwait(false);
        }
    }
}
