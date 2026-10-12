using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RetailFlow.Domain.Tenancy;
using RetailFlow.Infrastructure.Platform.Persistence;

namespace RetailFlow.Infrastructure.Tenancy.Persistence;

internal sealed class WarehouseConfiguration : IEntityTypeConfiguration<Warehouse>
{
    public void Configure(EntityTypeBuilder<Warehouse> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("Warehouse");
        builder.HasKey(w => w.Id);

        builder.Property(w => w.OrganizationId).IsRequired();
        builder.Property(w => w.BranchId).IsRequired();
        builder.Property(w => w.Code).HasMaxLength(32).IsRequired();
        builder.Property(w => w.Name).HasMaxLength(200).IsRequired();
        builder.Property(w => w.Type).HasConversion<byte>();
        builder.Property(w => w.ChangeSequence).IsRequired();
        builder.Property(w => w.ConcurrencyToken).UseConcurrencyToken();

        builder.HasOne<Branch>()
            .WithMany()
            .HasForeignKey(w => w.BranchId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(w => new { w.OrganizationId, w.Code })
            .IsUnique()
            .HasDatabaseName("UX_Warehouse_Org_Code");

        // One default per branch, enforced by the database rather than by application discipline:
        // stock movements resolve to "the" default, so two would be a silent mis-posting.
        builder.HasIndex(w => new { w.OrganizationId, w.BranchId, w.IsDefault })
            .IsUnique()
            .HasFilter("[IsDefault] = 1")
            .HasDatabaseName("UX_Warehouse_Branch_Default");

        builder.HasIndex(w => new { w.OrganizationId, w.BranchId })
            .HasDatabaseName("IX_Warehouse_Org_Branch");
        builder.HasIndex(w => new { w.OrganizationId, w.ChangeSequence })
            .HasDatabaseName("IX_Warehouse_Org_ChangeSequence");

        builder.Ignore(w => w.DomainEvents);
    }
}

internal sealed class PosTerminalConfiguration : IEntityTypeConfiguration<PosTerminal>
{
    public void Configure(EntityTypeBuilder<PosTerminal> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("PosTerminal");
        builder.HasKey(t => t.Id);

        builder.Property(t => t.OrganizationId).IsRequired();
        builder.Property(t => t.BranchId).IsRequired();
        builder.Property(t => t.Code).HasMaxLength(32).IsRequired();
        builder.Property(t => t.Name).HasMaxLength(200).IsRequired();
        builder.Property(t => t.ReceiptTemplate58).HasMaxLength(64);
        builder.Property(t => t.ReceiptTemplate80).HasMaxLength(64);
        builder.Property(t => t.AppVersion).HasMaxLength(32);
        builder.Property(t => t.DeviceFingerprint).HasMaxLength(256);
        builder.Property(t => t.ChangeSequence).IsRequired();
        builder.Property(t => t.ConcurrencyToken).UseConcurrencyToken();

        builder.HasOne<Branch>()
            .WithMany()
            .HasForeignKey(t => t.BranchId)
            .OnDelete(DeleteBehavior.Restrict);

        // No foreign key to Warehouse on purpose: the column is an override that may be cleared,
        // and a restrict-delete relationship here would tangle warehouse administration with
        // terminal configuration. The handlers check that the warehouse belongs to the branch.
        builder.HasIndex(t => new { t.OrganizationId, t.Code })
            .IsUnique()
            .HasDatabaseName("UX_PosTerminal_Org_Code");
        builder.HasIndex(t => new { t.OrganizationId, t.BranchId })
            .HasDatabaseName("IX_PosTerminal_Org_Branch");
        builder.HasIndex(t => new { t.OrganizationId, t.ChangeSequence })
            .HasDatabaseName("IX_PosTerminal_Org_ChangeSequence");

        builder.Ignore(t => t.DomainEvents);
    }
}

internal sealed class OrganizationSettingsConfiguration : IEntityTypeConfiguration<OrganizationSettings>
{
    public void Configure(EntityTypeBuilder<OrganizationSettings> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("OrganizationSettings");
        builder.HasKey(s => s.Id);

        builder.Property(s => s.OrganizationId).IsRequired();
        builder.Property(s => s.CashRoundingMode).HasConversion<byte?>();

        // Percentages are rates, not amounts (ADR-0006).
        builder.Property(s => s.MaxLineDiscountPercent).HasPrecision(9, 6);
        builder.Property(s => s.InvoiceNumberFormat).HasMaxLength(64);
        builder.Property(s => s.ReceiptTemplate58).HasMaxLength(64);
        builder.Property(s => s.ReceiptTemplate80).HasMaxLength(64);
        builder.Property(s => s.ReceiptFooterText).HasMaxLength(500);
        builder.Property(s => s.ChangeSequence).IsRequired();
        builder.Property(s => s.ConcurrencyToken).UseConcurrencyToken();

        builder.HasOne<Organization>()
            .WithMany()
            .HasForeignKey(s => s.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(s => s.OrganizationId)
            .IsUnique()
            .HasDatabaseName("UX_OrganizationSettings_Org");

        builder.Ignore(s => s.DomainEvents);
    }
}

internal sealed class BranchSettingsConfiguration : IEntityTypeConfiguration<BranchSettings>
{
    public void Configure(EntityTypeBuilder<BranchSettings> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("BranchSettings");
        builder.HasKey(s => s.Id);

        builder.Property(s => s.OrganizationId).IsRequired();
        builder.Property(s => s.BranchId).IsRequired();
        builder.Property(s => s.CashRoundingMode).HasConversion<byte?>();
        builder.Property(s => s.MaxLineDiscountPercent).HasPrecision(9, 6);
        builder.Property(s => s.ReceiptTemplate58).HasMaxLength(64);
        builder.Property(s => s.ReceiptTemplate80).HasMaxLength(64);
        builder.Property(s => s.ReceiptFooterText).HasMaxLength(500);
        builder.Property(s => s.ChangeSequence).IsRequired();
        builder.Property(s => s.ConcurrencyToken).UseConcurrencyToken();

        builder.HasOne<Branch>()
            .WithMany()
            .HasForeignKey(s => s.BranchId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(s => new { s.OrganizationId, s.BranchId })
            .IsUnique()
            .HasDatabaseName("UX_BranchSettings_Org_Branch");
        builder.HasIndex(s => new { s.OrganizationId, s.ChangeSequence })
            .HasDatabaseName("IX_BranchSettings_Org_ChangeSequence");

        builder.Ignore(s => s.DomainEvents);
    }
}

internal sealed class SubscriptionPlanConfiguration : IEntityTypeConfiguration<SubscriptionPlan>
{
    public void Configure(EntityTypeBuilder<SubscriptionPlan> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("SubscriptionPlan");
        builder.HasKey(p => p.Id);

        builder.Property(p => p.Code).HasMaxLength(32).IsRequired();
        builder.Property(p => p.Name).HasMaxLength(100).IsRequired();
        builder.Property(p => p.Description).HasMaxLength(500);
        builder.Property(p => p.MonthlyPrice).HasPrecision(19, 4);
        builder.Property(p => p.CurrencyCode).HasMaxLength(3).IsFixedLength().IsRequired();
        builder.Property(p => p.ConcurrencyToken).UseConcurrencyToken();

        builder.HasIndex(p => p.Code).IsUnique().HasDatabaseName("UX_SubscriptionPlan_Code");

        // Plans are platform reference data, so the features are loaded with them: a plan without
        // its limits is never useful, and the set is a handful of rows.
        builder.HasMany(p => p.Features)
            .WithOne()
            .HasForeignKey(f => f.PlanId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(p => p.Features).AutoInclude();

        builder.Ignore(p => p.DomainEvents);
    }
}

internal sealed class PlanFeatureConfiguration : IEntityTypeConfiguration<PlanFeature>
{
    public void Configure(EntityTypeBuilder<PlanFeature> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("PlanFeature");
        builder.HasKey(f => f.Id);

        builder.Property(f => f.PlanId).IsRequired();
        builder.Property(f => f.Code).HasConversion<byte>();

        builder.HasIndex(f => new { f.PlanId, f.Code })
            .IsUnique()
            .HasDatabaseName("UX_PlanFeature_Plan_Code");
    }
}

internal sealed class OrganizationSubscriptionConfiguration : IEntityTypeConfiguration<OrganizationSubscription>
{
    public void Configure(EntityTypeBuilder<OrganizationSubscription> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("OrganizationSubscription");
        builder.HasKey(s => s.Id);

        builder.Property(s => s.OrganizationId).IsRequired();
        builder.Property(s => s.PlanId).IsRequired();
        builder.Property(s => s.Status).HasConversion<byte>();
        builder.Property(s => s.CancellationReason).HasMaxLength(500);
        builder.Property(s => s.ChangeSequence).IsRequired();
        builder.Property(s => s.ConcurrencyToken).UseConcurrencyToken();

        builder.HasOne<Organization>()
            .WithMany()
            .HasForeignKey(s => s.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<SubscriptionPlan>()
            .WithMany()
            .HasForeignKey(s => s.PlanId)
            .OnDelete(DeleteBehavior.Restrict);

        // One subscription per organization: "which plan is this shop on" must have one answer.
        builder.HasIndex(s => s.OrganizationId)
            .IsUnique()
            .HasDatabaseName("UX_OrganizationSubscription_Org");

        builder.Ignore(s => s.DomainEvents);
    }
}
