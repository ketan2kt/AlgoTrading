using TradingSystem.Domain.Common;

namespace TradingSystem.Domain.Trading;

public sealed class BrokerAccessTokenSecret : MutableEntity
{
    public BrokerAccessTokenSecret(
        Guid id,
        string provider,
        string protectedValue,
        DateTimeOffset expiresAtUtc,
        string updatedBy,
        DateTimeOffset createdAtUtc) : base(id, createdAtUtc)
    {
        Provider = Require(provider, nameof(provider));
        ProtectedValue = Require(protectedValue, nameof(protectedValue));
        ExpiresAtUtc = expiresAtUtc.ToUniversalTime();
        UpdatedBy = Require(updatedBy, nameof(updatedBy));
    }

    public string Provider { get; private init; }
    public string ProtectedValue { get; private set; }
    public DateTimeOffset ExpiresAtUtc { get; private set; }
    public string UpdatedBy { get; private set; }

    public void Replace(
        string protectedValue,
        DateTimeOffset expiresAtUtc,
        string updatedBy,
        DateTimeOffset changedAtUtc)
    {
        ProtectedValue = Require(protectedValue, nameof(protectedValue));
        ExpiresAtUtc = expiresAtUtc.ToUniversalTime();
        UpdatedBy = Require(updatedBy, nameof(updatedBy));
        MarkUpdated(changedAtUtc);
    }

    private static string Require(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("Value is required.", parameterName)
            : value.Trim();
}
