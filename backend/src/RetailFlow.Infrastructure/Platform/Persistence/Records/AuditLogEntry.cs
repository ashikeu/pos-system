namespace RetailFlow.Infrastructure.Platform.Persistence.Records;

/// <summary>
/// An immutable record of something a user or the system did (FR-SEC-11, FR-TEN-07, NFR-09).
/// Written in the same transaction as the change it describes, so the trail and the data can never
/// disagree.
/// </summary>
public sealed class AuditLogEntry
{
    private AuditLogEntry()
    {
    }

    public Guid Id { get; private set; }

    public Guid? OrganizationId { get; private set; }

    public Guid? UserId { get; private set; }

    public string? UserName { get; private set; }

    public Guid? BranchId { get; private set; }

    public Guid? TerminalId { get; private set; }

    /// <summary>Dotted action code, e.g. <c>sales.void</c>.</summary>
    public string Action { get; private set; } = string.Empty;

    public string EntityType { get; private set; } = string.Empty;

    public Guid? EntityId { get; private set; }

    /// <summary>State before the change, as JSON, already stripped of secrets.</summary>
    public string? BeforeJson { get; private set; }

    /// <summary>State after the change, as JSON, already stripped of secrets.</summary>
    public string? AfterJson { get; private set; }

    /// <summary>Reason, approver, threshold and similar context, as JSON.</summary>
    public string? MetadataJson { get; private set; }

    public string? CorrelationId { get; private set; }

    public string? IpAddress { get; private set; }

    public string? UserAgent { get; private set; }

    public DateTimeOffset AtUtc { get; private set; }

    public static AuditLogEntry Create(
        Guid id,
        Guid? organizationId,
        Guid? userId,
        string? userName,
        Guid? branchId,
        Guid? terminalId,
        string action,
        string entityType,
        Guid? entityId,
        string? beforeJson,
        string? afterJson,
        string? metadataJson,
        string? correlationId,
        string? ipAddress,
        string? userAgent,
        DateTimeOffset atUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        ArgumentException.ThrowIfNullOrWhiteSpace(entityType);

        return new AuditLogEntry
        {
            Id = id,
            OrganizationId = organizationId,
            UserId = userId,
            UserName = userName,
            BranchId = branchId,
            TerminalId = terminalId,
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            BeforeJson = beforeJson,
            AfterJson = afterJson,
            MetadataJson = metadataJson,
            CorrelationId = correlationId,
            IpAddress = ipAddress,
            UserAgent = userAgent,
            AtUtc = atUtc,
        };
    }
}

/// <summary>
/// Per-tenant allocator for the incremental sync cursor. Kept as a table rather than a database
/// sequence so the meaning of the cursor is identical on every provider (ADR-0018).
/// </summary>
public sealed class TenantChangeSequence
{
    private TenantChangeSequence()
    {
    }

    public Guid OrganizationId { get; private set; }

    public long LastSequence { get; private set; }

    public static TenantChangeSequence Create(Guid organizationId) =>
        new() { OrganizationId = organizationId, LastSequence = 0 };

    /// <summary>
    /// Reserves <paramref name="count"/> consecutive values and returns the first of them. Called
    /// only by the allocator, which holds a row lock for the duration of the transaction.
    /// </summary>
    public long Reserve(int count)
    {
        if (count < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(count), count, "Count must be at least 1.");
        }

        var first = LastSequence + 1;
        LastSequence += count;
        return first;
    }
}
