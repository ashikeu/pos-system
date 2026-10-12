using Microsoft.EntityFrameworkCore;
using RetailFlow.Domain.Tenancy;
using RetailFlow.Infrastructure.Platform.Persistence.Records;

namespace RetailFlow.Infrastructure.Platform.Persistence.SqlServer;

/// <summary>
/// SQL Server physical tuning, kept apart from the portable model (ADR-0003). Everything here is
/// storage layout, never semantics: dropping this file would change performance, not behaviour.
/// </summary>
/// <remarks>
/// <para>
/// The important decision is clustering. SQL Server orders <c>uniqueidentifier</c> by its last six
/// bytes, so a UUIDv7 primary key — whose timestamp sits in the <em>first</em> bytes — is random
/// as far as the clustered index is concerned, and inserts would scatter across the table causing
/// page splits. Each high-volume table therefore keeps a non-clustered primary key and clusters on
/// a naturally increasing or naturally grouped key instead.
/// </para>
/// <para>
/// This corrects the claim in ADR-0005 that a time-ordered GUID is sequential for SQL Server
/// clustering; the identifier format stays, the physical layout is what changed.
/// </para>
/// </remarks>
internal static class SqlServerModelTuning
{
    public static void Apply(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        // Append-only queue: inserts land at the end, the dispatcher reads the oldest first.
        modelBuilder.Entity<OutboxMessage>(entity =>
        {
            entity.HasKey(m => m.Id).IsClustered(false);
            entity.HasIndex(m => new { m.CreatedAtUtc, m.Id }).IsClustered();

            // Only undelivered work is interesting; Processed (2) and DeadLettered (4) are excluded
            // so the dispatcher's index stays tiny no matter how many messages have been handled.
            entity.HasIndex(m => new { m.Status, m.NextAttemptAtUtc })
                .HasFilter("[Status] IN (0, 1, 3)");
        });

        // Lookups are always by (OrganizationId, Key); clustering on it makes the replay check a
        // single seek, which is on the critical path of every retried sale.
        modelBuilder.Entity<IdempotencyRecord>(entity =>
        {
            entity.HasKey(r => r.Id).IsClustered(false);
            entity.HasIndex(r => new { r.OrganizationId, r.Key }).IsClustered();
        });

        // Audit is written once and read by tenant and time.
        modelBuilder.Entity<AuditLogEntry>(entity =>
        {
            entity.HasKey(a => a.Id).IsClustered(false);
            entity.HasIndex(a => new { a.OrganizationId, a.AtUtc, a.Id }).IsClustered();
        });

        // Low-cardinality tables: clustering on the key is fine, fragmentation is irrelevant.
        modelBuilder.Entity<Organization>().HasKey(o => o.Id).IsClustered();
        modelBuilder.Entity<Branch>(entity =>
        {
            entity.HasKey(b => b.Id).IsClustered(false);
            entity.HasIndex(b => new { b.OrganizationId, b.Code }).IsClustered();
        });

        // Warehouses and terminals are read by tenant and branch on every sale, and there are few
        // of them, so they cluster on that access path rather than on the random key.
        modelBuilder.Entity<Warehouse>(entity =>
        {
            entity.HasKey(w => w.Id).IsClustered(false);
            entity.HasIndex(w => new { w.OrganizationId, w.Code }).IsClustered();
        });

        modelBuilder.Entity<PosTerminal>(entity =>
        {
            entity.HasKey(t => t.Id).IsClustered(false);
            entity.HasIndex(t => new { t.OrganizationId, t.Code }).IsClustered();
        });

        // One settings and one subscription row per tenant: the lookup is always by that key.
        modelBuilder.Entity<OrganizationSettings>(entity =>
        {
            entity.HasKey(s => s.Id).IsClustered(false);
            entity.HasIndex(s => s.OrganizationId).IsUnique().IsClustered();
        });

        modelBuilder.Entity<BranchSettings>(entity =>
        {
            entity.HasKey(s => s.Id).IsClustered(false);
            entity.HasIndex(s => new { s.OrganizationId, s.BranchId }).IsUnique().IsClustered();
        });

        modelBuilder.Entity<OrganizationSubscription>(entity =>
        {
            entity.HasKey(s => s.Id).IsClustered(false);
            entity.HasIndex(s => s.OrganizationId).IsUnique().IsClustered();
        });

        modelBuilder.Entity<SubscriptionPlan>(entity =>
        {
            entity.HasKey(p => p.Id).IsClustered(false);
            entity.HasIndex(p => p.Code).IsUnique().IsClustered();
        });
    }
}
