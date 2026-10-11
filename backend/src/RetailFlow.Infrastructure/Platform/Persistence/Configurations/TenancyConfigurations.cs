using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RetailFlow.Domain.Tenancy;

namespace RetailFlow.Infrastructure.Platform.Persistence.Configurations;

internal sealed class OrganizationConfiguration : IEntityTypeConfiguration<Organization>
{
    public void Configure(EntityTypeBuilder<Organization> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("Organization");
        builder.HasKey(o => o.Id);

        builder.Property(o => o.Code).HasMaxLength(32).IsRequired();
        builder.Property(o => o.Name).HasMaxLength(200).IsRequired();
        builder.Property(o => o.LegalName).HasMaxLength(200);
        builder.Property(o => o.BinNumber).HasMaxLength(32);
        builder.Property(o => o.CurrencyCode).HasMaxLength(3).IsFixedLength().IsRequired();
        builder.Property(o => o.TimeZoneId).HasMaxLength(64).IsRequired();
        builder.Property(o => o.DefaultLanguage).HasMaxLength(8).IsRequired();
        builder.Property(o => o.Status).HasConversion<byte>();
        builder.Property(o => o.SuspendedReason).HasMaxLength(500);
        builder.Property(o => o.ChangeSequence).IsRequired();
        builder.Property(o => o.ConcurrencyToken).UseConcurrencyToken();

        builder.HasIndex(o => o.Code).IsUnique().HasDatabaseName("UX_Organization_Code");
        builder.HasIndex(o => o.ChangeSequence).HasDatabaseName("IX_Organization_ChangeSequence");

        builder.Ignore(o => o.DomainEvents);
    }
}

internal sealed class BranchConfiguration : IEntityTypeConfiguration<Branch>
{
    public void Configure(EntityTypeBuilder<Branch> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("Branch");
        builder.HasKey(b => b.Id);

        builder.Property(b => b.OrganizationId).IsRequired();
        builder.Property(b => b.Code).HasMaxLength(32).IsRequired();
        builder.Property(b => b.Name).HasMaxLength(200).IsRequired();
        builder.Property(b => b.NameBn).HasMaxLength(200);
        builder.Property(b => b.AddressLine).HasMaxLength(500);
        builder.Property(b => b.City).HasMaxLength(100);
        builder.Property(b => b.PhoneNumber).HasMaxLength(32);
        builder.Property(b => b.TimeZoneId).HasMaxLength(64).IsRequired();
        builder.Property(b => b.BusinessDayStart).IsRequired();
        builder.Property(b => b.ChangeSequence).IsRequired();
        builder.Property(b => b.ConcurrencyToken).UseConcurrencyToken();

        builder.HasOne<Organization>()
            .WithMany()
            .HasForeignKey(b => b.OrganizationId)
            .OnDelete(DeleteBehavior.Restrict);

        // Codes are unique per tenant, never globally (ADR-0004).
        builder.HasIndex(b => new { b.OrganizationId, b.Code })
            .IsUnique()
            .HasDatabaseName("UX_Branch_Org_Code");
        builder.HasIndex(b => new { b.OrganizationId, b.ChangeSequence })
            .HasDatabaseName("IX_Branch_Org_ChangeSequence");

        builder.Ignore(b => b.DomainEvents);
    }
}
