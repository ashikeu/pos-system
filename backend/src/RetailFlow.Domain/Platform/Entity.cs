namespace RetailFlow.Domain.Platform;

/// <summary>
/// Base type for persisted objects. Identity is the key, not the reference, so two instances
/// loaded in different contexts compare equal.
/// </summary>
public abstract class Entity : IEquatable<Entity>
{
    protected Entity(Guid id)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Entity id must not be empty.", nameof(id));
        }

        Id = id;
    }

    /// <summary>Required by EF Core materialisation; never call this from domain code.</summary>
    protected Entity()
    {
    }

    public Guid Id { get; private set; }

    public bool Equals(Entity? other)
    {
        if (other is null)
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        return GetType() == other.GetType() && Id == other.Id && Id != Guid.Empty;
    }

    public override bool Equals(object? obj) => obj is Entity entity && Equals(entity);

    public override int GetHashCode() => HashCode.Combine(GetType(), Id);

    public override string ToString() => $"{GetType().Name}({Id})";
}
