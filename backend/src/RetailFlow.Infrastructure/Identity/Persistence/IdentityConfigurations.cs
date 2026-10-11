using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RetailFlow.Domain.Identity;
using RetailFlow.Domain.Tenancy;
using RetailFlow.Infrastructure.Platform.Persistence;

namespace RetailFlow.Infrastructure.Identity.Persistence;

internal sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("User");
        builder.HasKey(u => u.Id);

        builder.Property(u => u.Email).HasMaxLength(256).IsRequired();
        builder.Property(u => u.NormalizedEmail).HasMaxLength(256).IsRequired();
        builder.Property(u => u.FullName).HasMaxLength(200).IsRequired();
        builder.Property(u => u.PhoneNumber).HasMaxLength(32);
        builder.Property(u => u.PasswordHash).HasMaxLength(512).IsRequired();
        builder.Property(u => u.SecurityStamp).HasMaxLength(64).IsRequired();
        builder.Property(u => u.PosPinHash).HasMaxLength(512);
        builder.Property(u => u.ConcurrencyToken).UseConcurrencyToken();

        builder.HasOne<Organization>()
            .WithMany()
            .HasForeignKey(u => u.OrganizationId)
            .OnDelete(DeleteBehavior.Restrict);

        // One account per address per tenant. The same person at two shops has two accounts, which
        // keeps their permissions, lockouts and audit trail separate (ADR-0004).
        builder.HasIndex(u => new { u.OrganizationId, u.NormalizedEmail })
            .IsUnique()
            .HasDatabaseName("UX_User_Org_Email");

        builder.HasIndex(u => new { u.OrganizationId, u.ChangeSequence })
            .HasDatabaseName("IX_User_Org_ChangeSequence");

        builder.Ignore(u => u.DomainEvents);
    }
}

internal sealed class RoleConfiguration : IEntityTypeConfiguration<Role>
{
    public void Configure(EntityTypeBuilder<Role> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("Role");
        builder.HasKey(r => r.Id);

        builder.Property(r => r.Name).HasMaxLength(100).IsRequired();
        builder.Property(r => r.Description).HasMaxLength(500);
        builder.Property(r => r.ConcurrencyToken).UseConcurrencyToken();

        builder.HasIndex(r => new { r.OrganizationId, r.Name })
            .IsUnique()
            .HasDatabaseName("UX_Role_Org_Name");

        builder.Ignore(r => r.DomainEvents);
    }
}

internal sealed class PermissionConfiguration : IEntityTypeConfiguration<Permission>
{
    public void Configure(EntityTypeBuilder<Permission> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("Permission");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedNever();

        builder.Property(p => p.Code).HasMaxLength(100).IsRequired();
        builder.Property(p => p.Module).HasMaxLength(50).IsRequired();
        builder.Property(p => p.Description).HasMaxLength(300).IsRequired();

        // Global, not per tenant: the catalog is part of the application (ADR-0013).
        builder.HasIndex(p => p.Code).IsUnique().HasDatabaseName("UX_Permission_Code");
    }
}

internal sealed class RolePermissionConfiguration : IEntityTypeConfiguration<RolePermission>
{
    public void Configure(EntityTypeBuilder<RolePermission> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("RolePermission");
        builder.HasKey(rp => rp.Id);

        builder.Property(rp => rp.PermissionCode).HasMaxLength(100).IsRequired();
        builder.Property(rp => rp.ConcurrencyToken).UseConcurrencyToken();

        builder.HasOne<Role>()
            .WithMany()
            .HasForeignKey(rp => rp.RoleId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(rp => new { rp.RoleId, rp.PermissionCode })
            .IsUnique()
            .HasDatabaseName("UX_RolePermission_Role_Code");

        builder.Ignore(rp => rp.DomainEvents);
    }
}

internal sealed class UserRoleConfiguration : IEntityTypeConfiguration<UserRole>
{
    public void Configure(EntityTypeBuilder<UserRole> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("UserRole");
        builder.HasKey(ur => ur.Id);

        builder.Property(ur => ur.ConcurrencyToken).UseConcurrencyToken();

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(ur => ur.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Role>()
            .WithMany()
            .HasForeignKey(ur => ur.RoleId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(ur => new { ur.UserId, ur.RoleId })
            .IsUnique()
            .HasDatabaseName("UX_UserRole_User_Role");

        builder.Ignore(ur => ur.DomainEvents);
    }
}

internal sealed class UserBranchConfiguration : IEntityTypeConfiguration<UserBranch>
{
    public void Configure(EntityTypeBuilder<UserBranch> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("UserBranch");
        builder.HasKey(ub => ub.Id);

        builder.Property(ub => ub.ConcurrencyToken).UseConcurrencyToken();

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(ub => ub.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Branch>()
            .WithMany()
            .HasForeignKey(ub => ub.BranchId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Role>()
            .WithMany()
            .HasForeignKey(ub => ub.RoleId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(ub => new { ub.UserId, ub.BranchId })
            .IsUnique()
            .HasDatabaseName("UX_UserBranch_User_Branch");

        builder.Ignore(ub => ub.DomainEvents);
    }
}

internal sealed class UserBranchPermissionConfiguration : IEntityTypeConfiguration<UserBranchPermission>
{
    public void Configure(EntityTypeBuilder<UserBranchPermission> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("UserBranchPermission");
        builder.HasKey(p => p.Id);

        builder.Property(p => p.PermissionCode).HasMaxLength(100).IsRequired();
        builder.Property(p => p.Reason).HasMaxLength(500);
        builder.Property(p => p.ConcurrencyToken).UseConcurrencyToken();

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(p => p.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Branch>()
            .WithMany()
            .HasForeignKey(p => p.BranchId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(p => new { p.UserId, p.BranchId, p.PermissionCode })
            .IsUnique()
            .HasDatabaseName("UX_UserBranchPermission_User_Branch_Code");

        builder.Ignore(p => p.DomainEvents);
    }
}

internal sealed class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("RefreshToken");
        builder.HasKey(t => t.Id);

        builder.Property(t => t.TokenHash).HasMaxLength(64).IsFixedLength().IsRequired();
        builder.Property(t => t.RevokedReason).HasMaxLength(200);
        builder.Property(t => t.CreatedByIp).HasMaxLength(64);
        builder.Property(t => t.ConcurrencyToken).UseConcurrencyToken();

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(t => t.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // Lookup is always by hash, and a hash must be unique across the whole table: a collision
        // would let one tenant's token resolve to another's session.
        builder.HasIndex(t => t.TokenHash).IsUnique().HasDatabaseName("UX_RefreshToken_Hash");
        builder.HasIndex(t => t.FamilyId).HasDatabaseName("IX_RefreshToken_Family");
        builder.HasIndex(t => new { t.UserId, t.ExpiresAtUtc }).HasDatabaseName("IX_RefreshToken_User_Expires");

        builder.Ignore(t => t.DomainEvents);
    }
}

internal sealed class UserSessionConfiguration : IEntityTypeConfiguration<UserSession>
{
    public void Configure(EntityTypeBuilder<UserSession> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("UserSession");
        builder.HasKey(s => s.Id);

        builder.Property(s => s.IpAddress).HasMaxLength(64);
        builder.Property(s => s.UserAgent).HasMaxLength(512);
        builder.Property(s => s.EndedReason).HasMaxLength(200);
        builder.Property(s => s.ConcurrencyToken).UseConcurrencyToken();

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(s => s.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(s => new { s.UserId, s.EndedAtUtc }).HasDatabaseName("IX_UserSession_User_Ended");
        builder.HasIndex(s => s.RefreshFamilyId).HasDatabaseName("IX_UserSession_Family");

        builder.Ignore(s => s.DomainEvents);
    }
}

internal sealed class LoginAttemptConfiguration : IEntityTypeConfiguration<LoginAttempt>
{
    public void Configure(EntityTypeBuilder<LoginAttempt> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("LoginAttempt");
        builder.HasKey(a => a.Id);

        builder.Property(a => a.EmailAttempted).HasMaxLength(256).IsRequired();
        builder.Property(a => a.OrganizationCodeAttempted).HasMaxLength(32);
        builder.Property(a => a.IpAddress).HasMaxLength(64);
        builder.Property(a => a.UserAgent).HasMaxLength(512);
        builder.Property(a => a.FailureReason).HasConversion<byte>();

        // Rate limiting reads by address and by source address, both within a short window.
        builder.HasIndex(a => new { a.EmailAttempted, a.AtUtc }).HasDatabaseName("IX_LoginAttempt_Email_AtUtc");
        builder.HasIndex(a => new { a.IpAddress, a.AtUtc }).HasDatabaseName("IX_LoginAttempt_Ip_AtUtc");
        builder.HasIndex(a => new { a.OrganizationId, a.UserId, a.AtUtc })
            .HasDatabaseName("IX_LoginAttempt_Org_User_AtUtc");
    }
}

internal sealed class DeviceRegistrationConfiguration : IEntityTypeConfiguration<DeviceRegistration>
{
    public void Configure(EntityTypeBuilder<DeviceRegistration> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("DeviceRegistration");
        builder.HasKey(d => d.Id);

        builder.Property(d => d.Name).HasMaxLength(100).IsRequired();
        builder.Property(d => d.DeviceSecretHash).HasMaxLength(64).IsFixedLength().IsRequired();
        builder.Property(d => d.Fingerprint).HasMaxLength(256);
        builder.Property(d => d.AppVersion).HasMaxLength(32);
        builder.Property(d => d.RevokedReason).HasMaxLength(200);
        builder.Property(d => d.ConcurrencyToken).UseConcurrencyToken();

        builder.HasIndex(d => d.DeviceSecretHash).IsUnique().HasDatabaseName("UX_DeviceRegistration_SecretHash");
        builder.HasIndex(d => new { d.OrganizationId, d.TerminalId }).HasDatabaseName("IX_DeviceRegistration_Org_Terminal");

        builder.Ignore(d => d.DomainEvents);
    }
}

internal sealed class MfaSecretConfiguration : IEntityTypeConfiguration<MfaSecret>
{
    public void Configure(EntityTypeBuilder<MfaSecret> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("MfaSecret");
        builder.HasKey(s => s.Id);

        builder.Property(s => s.ProtectedSecret).HasMaxLength(512).IsRequired();
        builder.Property(s => s.ConcurrencyToken).UseConcurrencyToken();

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(s => s.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(s => s.UserId).HasDatabaseName("IX_MfaSecret_User");

        builder.Ignore(s => s.DomainEvents);
    }
}

internal sealed class UserTokenConfiguration : IEntityTypeConfiguration<UserToken>
{
    public void Configure(EntityTypeBuilder<UserToken> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("UserToken");
        builder.HasKey(t => t.Id);

        builder.Property(t => t.TokenHash).HasMaxLength(64).IsFixedLength().IsRequired();
        builder.Property(t => t.Purpose).HasConversion<byte>();
        builder.Property(t => t.RequestedByIp).HasMaxLength(64);
        builder.Property(t => t.ConcurrencyToken).UseConcurrencyToken();

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(t => t.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(t => new { t.UserId, t.Purpose, t.TokenHash })
            .IsUnique()
            .HasDatabaseName("UX_UserToken_User_Purpose_Hash");

        builder.HasIndex(t => t.ExpiresAtUtc).HasDatabaseName("IX_UserToken_ExpiresAt");

        builder.Ignore(t => t.DomainEvents);
    }
}
