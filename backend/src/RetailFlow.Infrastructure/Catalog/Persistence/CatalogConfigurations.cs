using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RetailFlow.Domain.Catalog;
using RetailFlow.Domain.Platform;
using RetailFlow.Domain.Tenancy;
using RetailFlow.Infrastructure.Platform.Persistence;

namespace RetailFlow.Infrastructure.Catalog.Persistence;

internal sealed class CategoryConfiguration : IEntityTypeConfiguration<Category>
{
    public void Configure(EntityTypeBuilder<Category> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("Category");
        builder.HasKey(c => c.Id);

        builder.Property(c => c.OrganizationId).IsRequired();
        builder.Property(c => c.Code).HasMaxLength(32).IsRequired();
        builder.Property(c => c.Name).HasMaxLength(200).IsRequired();
        builder.Property(c => c.NameBn).HasMaxLength(200);
        builder.Property(c => c.Status).HasConversion<byte>();
        builder.Property(c => c.ChangeSequence).IsRequired();
        builder.Property(c => c.ConcurrencyToken).UseConcurrencyToken();

        builder.HasOne<Category>()
            .WithMany()
            .HasForeignKey(c => c.ParentId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(c => new { c.OrganizationId, c.Code })
            .IsUnique()
            .HasDatabaseName("UX_Category_Org_Code");
        builder.HasIndex(c => new { c.OrganizationId, c.ParentId })
            .HasDatabaseName("IX_Category_Org_Parent");
        builder.HasIndex(c => new { c.OrganizationId, c.ChangeSequence })
            .HasDatabaseName("IX_Category_Org_ChangeSequence");

        builder.Ignore(c => c.DomainEvents);
    }
}

internal sealed class BrandConfiguration : IEntityTypeConfiguration<Brand>
{
    public void Configure(EntityTypeBuilder<Brand> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("Brand");
        builder.HasKey(b => b.Id);

        builder.Property(b => b.OrganizationId).IsRequired();
        builder.Property(b => b.Code).HasMaxLength(32).IsRequired();
        builder.Property(b => b.Name).HasMaxLength(200).IsRequired();
        builder.Property(b => b.NameBn).HasMaxLength(200);
        builder.Property(b => b.Status).HasConversion<byte>();
        builder.Property(b => b.ChangeSequence).IsRequired();
        builder.Property(b => b.ConcurrencyToken).UseConcurrencyToken();

        builder.HasIndex(b => new { b.OrganizationId, b.Code })
            .IsUnique()
            .HasDatabaseName("UX_Brand_Org_Code");
        builder.HasIndex(b => new { b.OrganizationId, b.ChangeSequence })
            .HasDatabaseName("IX_Brand_Org_ChangeSequence");

        builder.Ignore(b => b.DomainEvents);
    }
}

internal sealed class UnitOfMeasureConfiguration : IEntityTypeConfiguration<UnitOfMeasure>
{
    public void Configure(EntityTypeBuilder<UnitOfMeasure> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("UnitOfMeasure");
        builder.HasKey(u => u.Id);

        builder.Property(u => u.OrganizationId).IsRequired();
        builder.Property(u => u.Code).HasMaxLength(16).IsRequired();
        builder.Property(u => u.Name).HasMaxLength(100).IsRequired();
        builder.Property(u => u.NameBn).HasMaxLength(100);
        builder.Property(u => u.Kind).HasConversion<byte>();
        builder.Property(u => u.Status).HasConversion<byte>();
        builder.Property(u => u.ChangeSequence).IsRequired();
        builder.Property(u => u.ConcurrencyToken).UseConcurrencyToken();

        builder.HasIndex(u => new { u.OrganizationId, u.Code })
            .IsUnique()
            .HasDatabaseName("UX_UnitOfMeasure_Org_Code");
        builder.HasIndex(u => new { u.OrganizationId, u.ChangeSequence })
            .HasDatabaseName("IX_UnitOfMeasure_Org_ChangeSequence");

        builder.Ignore(u => u.DomainEvents);
    }
}

internal sealed class UomConversionConfiguration : IEntityTypeConfiguration<UomConversion>
{
    public void Configure(EntityTypeBuilder<UomConversion> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("UomConversion");
        builder.HasKey(c => c.Id);

        builder.Property(c => c.OrganizationId).IsRequired();

        // A conversion factor is a rate, not an amount (ADR-0006): 1 kg = 1000 g is exact, but
        // 1 bag = 33.333333 kg must not lose precision on the way into stock.
        builder.Property(c => c.Factor).HasPrecision(18, 6).IsRequired();
        builder.Property(c => c.ChangeSequence).IsRequired();
        builder.Property(c => c.ConcurrencyToken).UseConcurrencyToken();

        builder.HasOne<UnitOfMeasure>()
            .WithMany()
            .HasForeignKey(c => c.FromUomId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<UnitOfMeasure>()
            .WithMany()
            .HasForeignKey(c => c.ToUomId)
            .OnDelete(DeleteBehavior.Restrict);

        // One row per direction pair; the reverse is derived, never stored (see the aggregate).
        builder.HasIndex(c => new { c.OrganizationId, c.FromUomId, c.ToUomId })
            .IsUnique()
            .HasDatabaseName("UX_UomConversion_Org_From_To");

        builder.Ignore(c => c.DomainEvents);
    }
}

internal sealed class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("Product");
        builder.HasKey(p => p.Id);

        builder.Property(p => p.OrganizationId).IsRequired();
        builder.Property(p => p.Code).HasMaxLength(64).IsRequired();
        builder.Property(p => p.Name).HasMaxLength(300).IsRequired();
        builder.Property(p => p.NameBn).HasMaxLength(300);
        builder.Property(p => p.Description).HasMaxLength(2000);
        builder.Property(p => p.ProductType).HasConversion<byte>();
        builder.Property(p => p.Status).HasConversion<byte>();
        builder.Property(p => p.ChangeSequence).IsRequired();
        builder.Property(p => p.ConcurrencyToken).UseConcurrencyToken();

        builder.HasOne<Category>()
            .WithMany()
            .HasForeignKey(p => p.CategoryId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Brand>()
            .WithMany()
            .HasForeignKey(p => p.BrandId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<UnitOfMeasure>()
            .WithMany()
            .HasForeignKey(p => p.BaseUomId)
            .OnDelete(DeleteBehavior.Restrict);

        // No foreign key to TaxClass on purpose: it lives in the pricing module, and a database
        // constraint across module boundaries is the kind of coupling the module rule forbids.
        // The handlers check it, and a missing class resolves to no tax with a stated reason.
        builder.HasMany(p => p.Variants)
            .WithOne()
            .HasForeignKey(v => v.ProductId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(p => p.Attributes)
            .WithOne()
            .HasForeignKey(a => a.ProductId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(p => new { p.OrganizationId, p.Code })
            .IsUnique()
            .HasDatabaseName("UX_Product_Org_Code");
        builder.HasIndex(p => new { p.OrganizationId, p.CategoryId })
            .HasDatabaseName("IX_Product_Org_Category");
        builder.HasIndex(p => new { p.OrganizationId, p.BrandId })
            .HasDatabaseName("IX_Product_Org_Brand");
        builder.HasIndex(p => new { p.OrganizationId, p.Status })
            .HasDatabaseName("IX_Product_Org_Status");
        builder.HasIndex(p => new { p.OrganizationId, p.Name })
            .HasDatabaseName("IX_Product_Org_Name");
        builder.HasIndex(p => new { p.OrganizationId, p.ChangeSequence })
            .HasDatabaseName("IX_Product_Org_ChangeSequence");

        builder.Ignore(p => p.DomainEvents);
    }
}

internal sealed class ProductVariantConfiguration : IEntityTypeConfiguration<ProductVariant>
{
    public void Configure(EntityTypeBuilder<ProductVariant> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("ProductVariant");
        builder.HasKey(v => v.Id);

        builder.Property(v => v.OrganizationId).IsRequired();
        builder.Property(v => v.ProductId).IsRequired();
        builder.Property(v => v.Sku).HasMaxLength(64).IsRequired();
        builder.Property(v => v.Name).HasMaxLength(200);
        builder.Property(v => v.BasePrice).HasPrecision(19, 4).IsRequired();

        // Cost carries two more places than price (ADR-0006): it is divided by pack sizes and
        // multiplied by sold quantities, and rounding it early moves the margin.
        builder.Property(v => v.LastPurchaseCost).HasPrecision(19, 6);
        builder.Property(v => v.PackSize).HasPrecision(18, 3).IsRequired();
        builder.Property(v => v.WeightInGrams).HasPrecision(18, 3);
        builder.Property(v => v.Status).HasConversion<byte>();
        builder.Property(v => v.ChangeSequence).IsRequired();
        builder.Property(v => v.ConcurrencyToken).UseConcurrencyToken();

        builder.HasOne<UnitOfMeasure>()
            .WithMany()
            .HasForeignKey(v => v.UomId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(v => v.Barcodes)
            .WithOne()
            .HasForeignKey(b => b.ProductVariantId)
            .OnDelete(DeleteBehavior.Cascade);

        // SKUs identify stock across the whole tenant, not within one product: a stock count sheet
        // and an import file both key on them alone.
        builder.HasIndex(v => new { v.OrganizationId, v.Sku })
            .IsUnique()
            .HasDatabaseName("UX_ProductVariant_Org_Sku");
        builder.HasIndex(v => new { v.OrganizationId, v.ProductId })
            .HasDatabaseName("IX_ProductVariant_Org_Product");

        // One default variant per product, enforced by the database rather than by discipline: a
        // bare product scan resolves to "the" default.
        builder.HasIndex(v => new { v.OrganizationId, v.ProductId, v.IsDefault })
            .IsUnique()
            .HasFilter("[IsDefault] = 1")
            .HasDatabaseName("UX_ProductVariant_Product_Default");
        builder.HasIndex(v => new { v.OrganizationId, v.ChangeSequence })
            .HasDatabaseName("IX_ProductVariant_Org_ChangeSequence");

        builder.Ignore(v => v.DomainEvents);
    }
}

internal sealed class BarcodeConfiguration : IEntityTypeConfiguration<Barcode>
{
    public void Configure(EntityTypeBuilder<Barcode> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("Barcode");
        builder.HasKey(b => b.Id);

        builder.Property(b => b.OrganizationId).IsRequired();
        builder.Property(b => b.ProductVariantId).IsRequired();
        builder.Property(b => b.Code).HasMaxLength(64).IsRequired();
        builder.Property(b => b.Type).HasConversion<byte>();
        builder.Property(b => b.PackQuantity).HasPrecision(18, 3).IsRequired();
        builder.Property(b => b.ChangeSequence).IsRequired();
        builder.Property(b => b.ConcurrencyToken).UseConcurrencyToken();

        // The index a scan hits. Unique per tenant, because a code that matched two variants would
        // make a scan ambiguous — at a till, that means the wrong price.
        builder.HasIndex(b => new { b.OrganizationId, b.Code })
            .IsUnique()
            .HasDatabaseName("UX_Barcode_Org_Code");
        builder.HasIndex(b => new { b.OrganizationId, b.ProductVariantId })
            .HasDatabaseName("IX_Barcode_Org_Variant");
        builder.HasIndex(b => new { b.OrganizationId, b.ChangeSequence })
            .HasDatabaseName("IX_Barcode_Org_ChangeSequence");

        builder.Ignore(b => b.DomainEvents);
    }
}

internal sealed class ProductAttributeConfiguration : IEntityTypeConfiguration<ProductAttribute>
{
    public void Configure(EntityTypeBuilder<ProductAttribute> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("ProductAttribute");
        builder.HasKey(a => a.Id);

        builder.Property(a => a.OrganizationId).IsRequired();
        builder.Property(a => a.ProductId).IsRequired();
        builder.Property(a => a.Name).HasMaxLength(64).IsRequired();
        builder.Property(a => a.Value).HasMaxLength(256).IsRequired();
        builder.Property(a => a.ChangeSequence).IsRequired();
        builder.Property(a => a.ConcurrencyToken).UseConcurrencyToken();

        builder.HasIndex(a => new { a.OrganizationId, a.ProductId, a.Name })
            .IsUnique()
            .HasDatabaseName("UX_ProductAttribute_Product_Name");

        builder.Ignore(a => a.DomainEvents);
    }
}

internal sealed class ProductImageConfiguration : IEntityTypeConfiguration<ProductImage>
{
    public void Configure(EntityTypeBuilder<ProductImage> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("ProductImage");
        builder.HasKey(i => i.Id);

        builder.Property(i => i.OrganizationId).IsRequired();
        builder.Property(i => i.ProductId).IsRequired();

        // A key in the configured file store, never the bytes: a database that holds images is a
        // database nobody can restore quickly.
        builder.Property(i => i.StorageKey).HasMaxLength(512).IsRequired();
        builder.Property(i => i.AltText).HasMaxLength(200);
        builder.Property(i => i.ChangeSequence).IsRequired();
        builder.Property(i => i.ConcurrencyToken).UseConcurrencyToken();

        builder.HasOne<Product>()
            .WithMany()
            .HasForeignKey(i => i.ProductId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(i => new { i.OrganizationId, i.ProductId })
            .HasDatabaseName("IX_ProductImage_Org_Product");

        builder.Ignore(i => i.DomainEvents);
    }
}

internal sealed class ProductBranchSettingConfiguration : IEntityTypeConfiguration<ProductBranchSetting>
{
    public void Configure(EntityTypeBuilder<ProductBranchSetting> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("ProductBranchSetting");
        builder.HasKey(s => s.Id);

        builder.Property(s => s.OrganizationId).IsRequired();
        builder.Property(s => s.ProductVariantId).IsRequired();
        builder.Property(s => s.BranchId).IsRequired();
        builder.Property(s => s.MinStock).HasPrecision(18, 3);
        builder.Property(s => s.ReorderLevel).HasPrecision(18, 3);
        builder.Property(s => s.ReorderQuantity).HasPrecision(18, 3);
        builder.Property(s => s.ChangeSequence).IsRequired();
        builder.Property(s => s.ConcurrencyToken).UseConcurrencyToken();

        builder.HasOne<ProductVariant>()
            .WithMany()
            .HasForeignKey(s => s.ProductVariantId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Branch>()
            .WithMany()
            .HasForeignKey(s => s.BranchId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(s => new { s.OrganizationId, s.ProductVariantId, s.BranchId })
            .IsUnique()
            .HasDatabaseName("UX_ProductBranchSetting_Variant_Branch");
        builder.HasIndex(s => new { s.OrganizationId, s.BranchId })
            .HasDatabaseName("IX_ProductBranchSetting_Org_Branch");

        builder.Ignore(s => s.DomainEvents);
    }
}
