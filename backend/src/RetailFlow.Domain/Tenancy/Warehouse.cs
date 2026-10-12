using RetailFlow.Domain.Platform;

namespace RetailFlow.Domain.Tenancy;

/// <summary>
/// What a warehouse is used for. Damage and Transit are real stock locations rather than flags on a
/// movement: a write-off moves stock to Damage and a transfer parks it in Transit, so the ledger
/// always balances and "where is it now" has an answer at every moment (ADR-0008).
/// </summary>
public enum WarehouseType
{
    /// <summary>Back store or main stock room of a branch.</summary>
    Main = 0,

    /// <summary>Shop floor stock that the POS sells from.</summary>
    Sales = 1,

    /// <summary>Holds damaged or expired stock pending disposal.</summary>
    Damage = 2,

    /// <summary>Holds stock dispatched from one branch and not yet received by another.</summary>
    Transit = 3,
}

/// <summary>
/// A stock location inside a branch (FR-TEN-01). Every inventory movement names a warehouse, so a
/// branch that sells needs at least one, and the default is what the POS uses when a sale does not
/// say otherwise.
/// </summary>
public sealed class Warehouse : TenantEntity
{
    private Warehouse()
    {
    }

    private Warehouse(
        Guid id,
        Guid organizationId,
        Guid branchId,
        string code,
        string name,
        WarehouseType type,
        bool isDefault)
        : base(id, organizationId)
    {
        if (branchId == Guid.Empty)
        {
            throw new ArgumentException("Branch id must not be empty.", nameof(branchId));
        }

        BranchId = branchId;
        Code = code;
        Name = name;
        Type = type;
        IsDefault = isDefault;
        IsActive = true;
    }

    public Guid BranchId { get; private set; }

    /// <summary>Short code, unique within the organization.</summary>
    public string Code { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    public WarehouseType Type { get; private set; }

    /// <summary>
    /// The warehouse this branch uses when nothing more specific is chosen. Exactly one per branch
    /// is marked, which <see cref="ClearDefault"/> and <see cref="MakeDefault"/> maintain together.
    /// </summary>
    public bool IsDefault { get; private set; }

    public bool IsActive { get; private set; }

    public static Warehouse Create(
        Guid id,
        Guid organizationId,
        Guid branchId,
        string code,
        string name,
        WarehouseType type = WarehouseType.Main,
        bool isDefault = false)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new ArgumentException("Warehouse code is required.", nameof(code));
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Warehouse name is required.", nameof(name));
        }

        // Transit stock belongs to no branch's sellable inventory, so making it a branch default
        // would route sales at that till into a location they can never be picked from.
        if (isDefault && type is WarehouseType.Damage or WarehouseType.Transit)
        {
            throw new DomainException("A damage or transit warehouse cannot be the branch default.");
        }

        return new Warehouse(
            id,
            organizationId,
            branchId,
            code.Trim().ToUpperInvariant(),
            name.Trim(),
            type,
            isDefault);
    }

    public void Rename(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Warehouse name is required.", nameof(name));
        }

        Name = name.Trim();
    }

    public void MakeDefault()
    {
        if (Type is WarehouseType.Damage or WarehouseType.Transit)
        {
            throw new DomainException("A damage or transit warehouse cannot be the branch default.");
        }

        if (!IsActive)
        {
            throw new DomainException("An inactive warehouse cannot be the branch default.");
        }

        IsDefault = true;
    }

    public void ClearDefault() => IsDefault = false;

    public void Deactivate()
    {
        if (IsDefault)
        {
            throw new DomainException(
                "Make another warehouse the branch default before deactivating this one.");
        }

        IsActive = false;
    }

    public void Activate() => IsActive = true;
}
