using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RetailFlow.Infrastructure.Platform.Persistence.Records;

namespace RetailFlow.Infrastructure.Platform.Persistence.Configurations;

internal sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("OutboxMessage");
        builder.HasKey(m => m.Id);

        builder.Property(m => m.MessageType).HasMaxLength(200).IsRequired();
        builder.Property(m => m.PayloadJson).IsLargeText().IsRequired();
        builder.Property(m => m.Status).HasConversion<byte>();
        builder.Property(m => m.LeaseOwner).HasMaxLength(100);
        builder.Property(m => m.LastError).HasMaxLength(4000);
        builder.Property(m => m.CorrelationId).HasMaxLength(64);
        builder.Property(m => m.ConcurrencyToken).UseConcurrencyToken();

        // The dispatcher polls this index constantly; SqlServerModelTuning narrows it with a
        // filter so it stays small once millions of messages have been processed.
        builder.HasIndex(m => new { m.Status, m.NextAttemptAtUtc })
            .HasDatabaseName("IX_OutboxMessage_Status_NextAttempt");

        builder.HasIndex(m => new { m.CreatedAtUtc, m.Id }).HasDatabaseName("IX_OutboxMessage_CreatedAt");
        builder.HasIndex(m => m.OrganizationId).HasDatabaseName("IX_OutboxMessage_Org");
    }
}

internal sealed class IdempotencyRecordConfiguration : IEntityTypeConfiguration<IdempotencyRecord>
{
    public void Configure(EntityTypeBuilder<IdempotencyRecord> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("IdempotencyRecord");
        builder.HasKey(r => r.Id);

        builder.Property(r => r.Key).HasMaxLength(128).IsRequired();
        builder.Property(r => r.Endpoint).HasMaxLength(256).IsRequired();
        builder.Property(r => r.RequestHash).HasMaxLength(64).IsFixedLength().IsRequired();
        builder.Property(r => r.Status).HasConversion<byte>();
        builder.Property(r => r.ResponseJson).IsLargeText();

        // The guarantee that a retried operation cannot run twice is this unique index, not
        // application code: two concurrent calls with one key, one insert wins (ADR-0010).
        builder.HasIndex(r => new { r.OrganizationId, r.Key })
            .IsUnique()
            .HasDatabaseName("UX_IdempotencyRecord_Org_Key");

        builder.HasIndex(r => r.ExpiresAtUtc).HasDatabaseName("IX_IdempotencyRecord_ExpiresAt");
    }
}

internal sealed class AuditLogEntryConfiguration : IEntityTypeConfiguration<AuditLogEntry>
{
    public void Configure(EntityTypeBuilder<AuditLogEntry> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("AuditLog");
        builder.HasKey(a => a.Id);

        builder.Property(a => a.Action).HasMaxLength(100).IsRequired();
        builder.Property(a => a.EntityType).HasMaxLength(100).IsRequired();
        builder.Property(a => a.UserName).HasMaxLength(200);
        builder.Property(a => a.BeforeJson).IsLargeText();
        builder.Property(a => a.AfterJson).IsLargeText();
        builder.Property(a => a.MetadataJson).IsLargeText();
        builder.Property(a => a.CorrelationId).HasMaxLength(64);
        builder.Property(a => a.IpAddress).HasMaxLength(64);
        builder.Property(a => a.UserAgent).HasMaxLength(512);

        builder.HasIndex(a => new { a.OrganizationId, a.AtUtc, a.Id }).HasDatabaseName("IX_AuditLog_Org_AtUtc");
        builder.HasIndex(a => new { a.OrganizationId, a.EntityType, a.EntityId })
            .HasDatabaseName("IX_AuditLog_Org_Entity");
        builder.HasIndex(a => new { a.OrganizationId, a.UserId, a.AtUtc })
            .HasDatabaseName("IX_AuditLog_Org_User_AtUtc");
    }
}

internal sealed class TenantChangeSequenceConfiguration : IEntityTypeConfiguration<TenantChangeSequence>
{
    public void Configure(EntityTypeBuilder<TenantChangeSequence> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("TenantChangeSequence");
        builder.HasKey(s => s.OrganizationId);
        builder.Property(s => s.OrganizationId).ValueGeneratedNever();
        builder.Property(s => s.LastSequence).IsRequired();
    }
}
