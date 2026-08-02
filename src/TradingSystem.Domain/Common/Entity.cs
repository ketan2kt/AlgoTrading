namespace TradingSystem.Domain.Common;

public abstract class Entity
{
    protected Entity(Guid id)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Entity identifier cannot be empty.", nameof(id));
        }

        Id = id;
    }

    public Guid Id { get; private init; }
}

public abstract class MutableEntity : Entity
{
    protected MutableEntity(Guid id, DateTimeOffset createdAtUtc) : base(id)
    {
        CreatedAtUtc = createdAtUtc.ToUniversalTime();
        UpdatedAtUtc = CreatedAtUtc;
        ConcurrencyToken = Guid.NewGuid();
    }

    public DateTimeOffset CreatedAtUtc { get; private init; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public Guid ConcurrencyToken { get; private set; }

    public void MarkUpdated(DateTimeOffset updatedAtUtc)
    {
        var utc = updatedAtUtc.ToUniversalTime();
        if (utc < CreatedAtUtc)
        {
            throw new ArgumentOutOfRangeException(
                nameof(updatedAtUtc),
                "Update time cannot precede creation time.");
        }

        UpdatedAtUtc = utc;
        ConcurrencyToken = Guid.NewGuid();
    }
}

public interface IAppendOnlyEntity;

