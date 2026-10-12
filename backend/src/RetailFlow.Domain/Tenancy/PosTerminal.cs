using RetailFlow.Domain.Platform;

namespace RetailFlow.Domain.Tenancy;

/// <summary>
/// A till. Terminals matter beyond bookkeeping: receipt numbering is allocated per terminal so two
/// offline tills cannot mint the same invoice number (ADR-0017), and a sale is always attributed to
/// one, which is what makes a shift reconcilable.
/// </summary>
public sealed class PosTerminal : TenantEntity
{
    private PosTerminal()
    {
    }

    private PosTerminal(Guid id, Guid organizationId, Guid branchId, string code, string name)
        : base(id, organizationId)
    {
        if (branchId == Guid.Empty)
        {
            throw new ArgumentException("Branch id must not be empty.", nameof(branchId));
        }

        BranchId = branchId;
        Code = code;
        Name = name;
        IsActive = true;
    }

    public Guid BranchId { get; private set; }

    /// <summary>Short code, unique within the organization; printed on receipts and Z-reports.</summary>
    public string Code { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    /// <summary>
    /// Warehouse this till sells from. Null means "use the branch default", which is what most
    /// shops want; a supermarket with a separate floor stock location sets it explicitly.
    /// </summary>
    public Guid? DefaultWarehouseId { get; private set; }

    /// <summary>58 mm receipt template name, overriding the branch and organization setting.</summary>
    public string? ReceiptTemplate58 { get; private set; }

    /// <summary>80 mm receipt template name, overriding the branch and organization setting.</summary>
    public string? ReceiptTemplate80 { get; private set; }

    public bool IsActive { get; private set; }

    /// <summary>Last time this terminal completed a sync, used by the stale-terminal report.</summary>
    public DateTimeOffset? LastSyncAtUtc { get; private set; }

    /// <summary>Client version last seen, so a shop running an old build can be spotted.</summary>
    public string? AppVersion { get; private set; }

    /// <summary>
    /// Fingerprint of the machine this terminal was last registered from. Advisory only: it helps
    /// an owner notice that a till moved, and is never treated as authentication (ADR-0012).
    /// </summary>
    public string? DeviceFingerprint { get; private set; }

    public static PosTerminal Create(Guid id, Guid organizationId, Guid branchId, string code, string name)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new ArgumentException("Terminal code is required.", nameof(code));
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Terminal name is required.", nameof(name));
        }

        return new PosTerminal(id, organizationId, branchId, code.Trim().ToUpperInvariant(), name.Trim());
    }

    public void UpdateDetails(string name, Guid? defaultWarehouseId, string? receiptTemplate58, string? receiptTemplate80)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Terminal name is required.", nameof(name));
        }

        Name = name.Trim();
        DefaultWarehouseId = defaultWarehouseId;
        ReceiptTemplate58 = Trimmed(receiptTemplate58);
        ReceiptTemplate80 = Trimmed(receiptTemplate80);
    }

    /// <summary>Records a completed registration from a device (FR-TEN-01, ADR-0012).</summary>
    public void RecordRegistration(DateTimeOffset atUtc, string? appVersion, string? deviceFingerprint)
    {
        LastSyncAtUtc = atUtc;
        AppVersion = Trimmed(appVersion);
        DeviceFingerprint = Trimmed(deviceFingerprint);
    }

    /// <summary>Records that this terminal synced, called by the sync engine (Phase 12).</summary>
    public void RecordSync(DateTimeOffset atUtc, string? appVersion = null)
    {
        LastSyncAtUtc = atUtc;

        if (!string.IsNullOrWhiteSpace(appVersion))
        {
            AppVersion = appVersion.Trim();
        }
    }

    public void Deactivate() => IsActive = false;

    public void Activate() => IsActive = true;

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
