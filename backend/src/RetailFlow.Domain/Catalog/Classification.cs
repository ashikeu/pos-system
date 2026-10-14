using RetailFlow.Domain.Platform;

namespace RetailFlow.Domain.Catalog;

/// <summary>
/// Lifecycle of a catalog record. Nothing in the catalog is deleted: a product that has ever been
/// sold is referenced by sale lines, stock movements and tax filings, so withdrawing it has to mean
/// "stop offering it", never "remove it" (NFR-09).
/// </summary>
public enum CatalogStatus
{
    /// <summary>Being prepared; not sellable and hidden from the POS.</summary>
    Draft = 0,

    /// <summary>In use.</summary>
    Active = 1,

    /// <summary>Temporarily withdrawn; still reportable and still holds stock.</summary>
    Inactive = 2,

    /// <summary>Permanently withdrawn. History stays; it can never be sold again.</summary>
    Archived = 3,
}

/// <summary>
/// A product category, optionally nested. Depth is bounded: a tree deep enough to need recursion
/// is a sign someone is modelling attributes as categories, and every POS screen that renders it
/// pays for the depth.
/// </summary>
public sealed class Category : TenantEntity
{
    /// <summary>Maximum nesting depth, counting the root as level 1.</summary>
    public const int MaxDepth = 4;

    private Category()
    {
    }

    private Category(Guid id, Guid organizationId, string code, string name, Guid? parentId, int depth)
        : base(id, organizationId)
    {
        Code = code;
        Name = name;
        ParentId = parentId;
        Depth = depth;
        Status = CatalogStatus.Active;
    }

    public string Code { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    public string? NameBn { get; private set; }

    public Guid? ParentId { get; private set; }

    /// <summary>1 for a root category. Stored so a listing can indent without walking parents.</summary>
    public int Depth { get; private set; }

    public int SortOrder { get; private set; }

    public CatalogStatus Status { get; private set; }

    public static Category Create(
        Guid id,
        Guid organizationId,
        string code,
        string name,
        Category? parent = null,
        int sortOrder = 0)
    {
        var depth = 1;

        if (parent is not null)
        {
            if (parent.OrganizationId != organizationId)
            {
                throw new TenantMismatchException(
                    parent.OrganizationId, organizationId, nameof(Category), parent.Id);
            }

            if (parent.Depth >= MaxDepth)
            {
                throw new DomainException($"Categories may not nest deeper than {MaxDepth} levels.");
            }

            depth = parent.Depth + 1;
        }

        return new Category(
            id,
            organizationId,
            CatalogText.Code(code, nameof(code)),
            CatalogText.Required(name, nameof(name), 200),
            parent?.Id,
            depth)
        {
            SortOrder = sortOrder,
        };
    }

    public void Rename(string name, string? nameBn)
    {
        Name = CatalogText.Required(name, nameof(name), 200);
        NameBn = CatalogText.Optional(nameBn, 200);
    }

    public void Reorder(int sortOrder) => SortOrder = sortOrder;

    /// <summary>
    /// Moves the category under another parent, or to the root when <paramref name="parent"/> is
    /// null. Refuses a cycle and refuses to exceed the depth limit.
    /// </summary>
    public void MoveUnder(Category? parent, int descendantDepth = 0)
    {
        if (parent is null)
        {
            ParentId = null;
            Depth = 1;
            return;
        }

        if (parent.Id == Id)
        {
            throw new DomainException("A category cannot be its own parent.");
        }

        if (parent.OrganizationId != OrganizationId)
        {
            throw new TenantMismatchException(parent.OrganizationId, OrganizationId, nameof(Category), parent.Id);
        }

        // descendantDepth is how far the deepest child sits below this category; the caller reads
        // it, because the aggregate does not hold the tree.
        if (parent.Depth + 1 + descendantDepth > MaxDepth)
        {
            throw new DomainException($"Categories may not nest deeper than {MaxDepth} levels.");
        }

        ParentId = parent.Id;
        Depth = parent.Depth + 1;
    }

    public void Activate() => Status = CatalogStatus.Active;

    public void Deactivate() => Status = CatalogStatus.Inactive;

    public void Archive() => Status = CatalogStatus.Archived;
}

/// <summary>A manufacturer or brand, used for filtering and brand-level reporting.</summary>
public sealed class Brand : TenantEntity
{
    private Brand()
    {
    }

    private Brand(Guid id, Guid organizationId, string code, string name)
        : base(id, organizationId)
    {
        Code = code;
        Name = name;
        Status = CatalogStatus.Active;
    }

    public string Code { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    public string? NameBn { get; private set; }

    public CatalogStatus Status { get; private set; }

    public static Brand Create(Guid id, Guid organizationId, string code, string name) =>
        new(id, organizationId, CatalogText.Code(code, nameof(code)), CatalogText.Required(name, nameof(name), 200));

    public void Rename(string name, string? nameBn)
    {
        Name = CatalogText.Required(name, nameof(name), 200);
        NameBn = CatalogText.Optional(nameBn, 200);
    }

    public void Activate() => Status = CatalogStatus.Active;

    public void Deactivate() => Status = CatalogStatus.Inactive;

    public void Archive() => Status = CatalogStatus.Archived;
}

/// <summary>
/// Normalisation shared by the catalog entities. Codes, SKUs and barcodes are matched exactly by
/// scanners and import files, so they are trimmed and upper-cased in one place rather than in
/// fifteen.
/// </summary>
internal static class CatalogText
{
    public static string Code(string value, string parameterName, int maxLength = 32)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"{parameterName} is required.", parameterName);
        }

        var trimmed = value.Trim().ToUpperInvariant();

        if (trimmed.Length > maxLength)
        {
            throw new ArgumentException($"{parameterName} must be at most {maxLength} characters.", parameterName);
        }

        return trimmed;
    }

    public static string Required(string value, string parameterName, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"{parameterName} is required.", parameterName);
        }

        var trimmed = value.Trim();
        return trimmed.Length > maxLength ? trimmed[..maxLength] : trimmed;
    }

    public static string? Optional(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length > maxLength ? trimmed[..maxLength] : trimmed;
    }
}
