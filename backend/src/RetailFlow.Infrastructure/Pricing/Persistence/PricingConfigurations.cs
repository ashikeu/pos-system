using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RetailFlow.Domain.Catalog;
using RetailFlow.Domain.Pricing;
using RetailFlow.Domain.Tenancy;
using RetailFlow.Infrastructure.Platform.Persistence;

namespace RetailFlow.Infrastructure.Pricing.Persistence;

internal sealed class TaxClassConfiguration : IEntityTypeConfiguration<TaxClass>
{
    public void Configure(EntityTypeBuilder<TaxClass> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("TaxClass");
        builder.HasKey(t => t.Id);

        builder.Property(t => t.OrganizationId).IsRequired();
        builder.Property(t => t.Code).HasMaxLength(32).IsRequired();
        builder.Property(t => t.Name).HasMaxLength(100).IsRequired();
        builder.Property(t => t.Treatment).HasConversion<byte>();
        builder.Property(t => t.ExemptionReason).HasMaxLength(256);
        builder.Property(t => t.ChangeSequence).IsRequired();
        builder.Property(t => t.ConcurrencyToken).UseConcurrencyToken();

        builder.HasIndex(t => new { t.OrganizationId, t.Code })
            .IsUnique()
            .HasDatabaseName("UX_TaxClass_Org_Code");

        // One default class per tenant: a product with no class resolves to "the" default.
        builder.HasIndex(t => new { t.OrganizationId, t.IsDefault })
            .IsUnique()
            .HasFilter("[IsDefault] = 1")
            .HasDatabaseName("UX_TaxClass_Org_Default");
        builder.HasIndex(t => new { t.OrganizationId, t.ChangeSequence })
            .HasDatabaseName("IX_TaxClass_Org_ChangeSequence");

        builder.Ignore(t => t.DomainEvents);
    }
}

internal sealed class TaxRuleConfiguration : IEntityTypeConfiguration<TaxRule>
{
    public void Configure(EntityTypeBuilder<TaxRule> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("TaxRule");
        builder.HasKey(r => r.Id);

        builder.Property(r => r.OrganizationId).IsRequired();
        builder.Property(r => r.TaxClassId).IsRequired();
        builder.Property(r => r.Name).HasMaxLength(100).IsRequired();
        builder.Property(r => r.ChangeSequence).IsRequired();
        builder.Property(r => r.ConcurrencyToken).UseConcurrencyToken();

        builder.HasOne<TaxClass>()
            .WithMany()
            .HasForeignKey(r => r.TaxClassId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Branch>()
            .WithMany()
            .HasForeignKey(r => r.BranchId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(r => r.Versions)
            .WithOne()
            .HasForeignKey(v => v.TaxRuleId)
            .OnDelete(DeleteBehavior.Cascade);

        // One rule per class per branch, and — because SQL Server treats NULL as a value in a
        // unique index — exactly one organization-wide rule per class as well.
        builder.HasIndex(r => new { r.OrganizationId, r.TaxClassId, r.BranchId })
            .IsUnique()
            .HasDatabaseName("UX_TaxRule_Org_Class_Branch");

        builder.Ignore(r => r.DomainEvents);
    }
}

internal sealed class TaxRuleVersionConfiguration : IEntityTypeConfiguration<TaxRuleVersion>
{
    public void Configure(EntityTypeBuilder<TaxRuleVersion> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("TaxRuleVersion");
        builder.HasKey(v => v.Id);

        builder.Property(v => v.OrganizationId).IsRequired();
        builder.Property(v => v.TaxRuleId).IsRequired();
        builder.Property(v => v.Version).IsRequired();

        // Rates are rates, not amounts (ADR-0006): 0.150000, never 0.1500.
        builder.Property(v => v.VatRate).HasPrecision(9, 6).IsRequired();
        builder.Property(v => v.SdRate).HasPrecision(9, 6).IsRequired();
        builder.Property(v => v.SroReference).HasMaxLength(64);
        builder.Property(v => v.Notes).HasMaxLength(500);
        builder.Property(v => v.ChangeSequence).IsRequired();
        builder.Property(v => v.ConcurrencyToken).UseConcurrencyToken();

        builder.HasIndex(v => new { v.TaxRuleId, v.Version })
            .IsUnique()
            .HasDatabaseName("UX_TaxRuleVersion_Rule_Version");

        // Exactly one open-ended version per rule, so "the rate in force" cannot be ambiguous.
        builder.HasIndex(v => v.TaxRuleId)
            .IsUnique()
            .HasFilter("[EffectiveToUtc] IS NULL")
            .HasDatabaseName("UX_TaxRuleVersion_Rule_Current");

        builder.HasIndex(v => new { v.OrganizationId, v.EffectiveFromUtc })
            .HasDatabaseName("IX_TaxRuleVersion_Org_EffectiveFrom");

        builder.Ignore(v => v.DomainEvents);
    }
}

internal sealed class PriceListConfiguration : IEntityTypeConfiguration<PriceList>
{
    public void Configure(EntityTypeBuilder<PriceList> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("PriceList");
        builder.HasKey(l => l.Id);

        builder.Property(l => l.OrganizationId).IsRequired();
        builder.Property(l => l.Code).HasMaxLength(32).IsRequired();
        builder.Property(l => l.Name).HasMaxLength(100).IsRequired();
        builder.Property(l => l.ChangeSequence).IsRequired();
        builder.Property(l => l.ConcurrencyToken).UseConcurrencyToken();

        builder.HasOne<Branch>()
            .WithMany()
            .HasForeignKey(l => l.BranchId)
            .OnDelete(DeleteBehavior.Restrict);

        // No foreign key to CustomerGroup: that table arrives in Phase 8, and the column is a
        // scope the resolver matches rather than a relationship it navigates.
        builder.HasMany(l => l.Items)
            .WithOne()
            .HasForeignKey(i => i.PriceListId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(l => new { l.OrganizationId, l.Code })
            .IsUnique()
            .HasDatabaseName("UX_PriceList_Org_Code");
        builder.HasIndex(l => new { l.OrganizationId, l.IsDefault })
            .IsUnique()
            .HasFilter("[IsDefault] = 1")
            .HasDatabaseName("UX_PriceList_Org_Default");
        builder.HasIndex(l => new { l.OrganizationId, l.CustomerGroupId, l.BranchId })
            .HasDatabaseName("IX_PriceList_Org_Scope");

        builder.Ignore(l => l.DomainEvents);
    }
}

internal sealed class PriceListItemConfiguration : IEntityTypeConfiguration<PriceListItem>
{
    public void Configure(EntityTypeBuilder<PriceListItem> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("PriceListItem");
        builder.HasKey(i => i.Id);

        builder.Property(i => i.OrganizationId).IsRequired();
        builder.Property(i => i.PriceListId).IsRequired();
        builder.Property(i => i.ProductVariantId).IsRequired();
        builder.Property(i => i.UnitPrice).HasPrecision(19, 4).IsRequired();
        builder.Property(i => i.MinQuantity).HasPrecision(18, 3).IsRequired();
        builder.Property(i => i.ChangeSequence).IsRequired();
        builder.Property(i => i.ConcurrencyToken).UseConcurrencyToken();

        builder.HasOne<ProductVariant>()
            .WithMany()
            .HasForeignKey(i => i.ProductVariantId)
            .OnDelete(DeleteBehavior.Cascade);

        // One row per variant per quantity tier, which is what makes "20 each, 18 from a dozen"
        // expressible without two rows fighting over the same tier.
        builder.HasIndex(i => new { i.PriceListId, i.ProductVariantId, i.MinQuantity })
            .IsUnique()
            .HasDatabaseName("UX_PriceListItem_List_Variant_MinQty");

        // The index the price resolver hits on every scan.
        builder.HasIndex(i => new { i.OrganizationId, i.ProductVariantId })
            .HasDatabaseName("IX_PriceListItem_Org_Variant");

        builder.Ignore(i => i.DomainEvents);
    }
}

internal sealed class BranchPriceOverrideConfiguration : IEntityTypeConfiguration<BranchPriceOverride>
{
    public void Configure(EntityTypeBuilder<BranchPriceOverride> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("BranchPriceOverride");
        builder.HasKey(p => p.Id);

        builder.Property(p => p.OrganizationId).IsRequired();
        builder.Property(p => p.BranchId).IsRequired();
        builder.Property(p => p.ProductVariantId).IsRequired();
        builder.Property(p => p.UnitPrice).HasPrecision(19, 4).IsRequired();
        builder.Property(p => p.ChangeSequence).IsRequired();
        builder.Property(p => p.ConcurrencyToken).UseConcurrencyToken();

        builder.HasOne<Branch>()
            .WithMany()
            .HasForeignKey(p => p.BranchId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<ProductVariant>()
            .WithMany()
            .HasForeignKey(p => p.ProductVariantId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(p => new { p.OrganizationId, p.BranchId, p.ProductVariantId })
            .IsUnique()
            .HasDatabaseName("UX_BranchPriceOverride_Branch_Variant");
        builder.HasIndex(p => new { p.OrganizationId, p.ChangeSequence })
            .HasDatabaseName("IX_BranchPriceOverride_Org_ChangeSequence");

        builder.Ignore(p => p.DomainEvents);
    }
}

internal sealed class DiscountRuleConfiguration : IEntityTypeConfiguration<DiscountRule>
{
    public void Configure(EntityTypeBuilder<DiscountRule> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("DiscountRule");
        builder.HasKey(r => r.Id);

        builder.Property(r => r.OrganizationId).IsRequired();
        builder.Property(r => r.Code).HasMaxLength(32).IsRequired();
        builder.Property(r => r.Name).HasMaxLength(100).IsRequired();
        builder.Property(r => r.Scope).HasConversion<byte>();
        builder.Property(r => r.Kind).HasConversion<byte>();

        // A percentage is a rate and an amount is money, and this column holds whichever the Kind
        // says. Six places cover both without a second column (ADR-0006).
        builder.Property(r => r.Value).HasPrecision(19, 6).IsRequired();
        builder.Property(r => r.MinQuantity).HasPrecision(18, 3);
        builder.Property(r => r.MinBasketValue).HasPrecision(19, 4);
        builder.Property(r => r.MaxDiscountAmount).HasPrecision(19, 4);
        builder.Property(r => r.RequiredPermission).HasMaxLength(64);
        builder.Property(r => r.ChangeSequence).IsRequired();
        builder.Property(r => r.ConcurrencyToken).UseConcurrencyToken();

        builder.HasOne<Branch>()
            .WithMany()
            .HasForeignKey(r => r.BranchId)
            .OnDelete(DeleteBehavior.Restrict);

        // TargetId is a variant, a category or a brand depending on Scope, so it carries no foreign
        // key; the handler checks it against the right table before the rule is created.
        builder.HasIndex(r => new { r.OrganizationId, r.Code })
            .IsUnique()
            .HasDatabaseName("UX_DiscountRule_Org_Code");
        builder.HasIndex(r => new { r.OrganizationId, r.Scope, r.TargetId })
            .HasDatabaseName("IX_DiscountRule_Org_Scope_Target");
        builder.HasIndex(r => new { r.OrganizationId, r.ChangeSequence })
            .HasDatabaseName("IX_DiscountRule_Org_ChangeSequence");

        builder.Ignore(r => r.DomainEvents);
    }
}
