using TradingSystem.Domain.Common;

namespace TradingSystem.Domain.Trading;

public sealed class ApplicationSetting : MutableEntity
{
    public ApplicationSetting(
        Guid id,
        TradingMode mode,
        string key,
        string valueJson,
        DateTimeOffset createdAtUtc) : base(id, createdAtUtc)
    {
        Mode = mode;
        Key = RequireText(key, nameof(key));
        ValueJson = RequireText(valueJson, nameof(valueJson));
    }

    public TradingMode Mode { get; private set; }

    public string Key { get; private set; }

    public string ValueJson { get; private set; }

    public void ChangeValue(string valueJson, DateTimeOffset changedAtUtc)
    {
        ValueJson = RequireText(valueJson, nameof(valueJson));
        MarkUpdated(changedAtUtc);
    }

    private static string RequireText(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("Value is required.", parameterName)
            : value.Trim();
}

public sealed class BrokerConnection : MutableEntity
{
    public BrokerConnection(
        Guid id,
        TradingMode mode,
        string provider,
        string secretReference,
        DateTimeOffset createdAtUtc) : base(id, createdAtUtc)
    {
        Mode = mode;
        Provider = provider;
        SecretReference = secretReference;
    }

    public TradingMode Mode { get; private init; }
    public string Provider { get; private set; }
    public string SecretReference { get; private set; }
    public bool IsEnabled { get; private set; }
    public DateTimeOffset? TokenExpiresAtUtc { get; private set; }
}

public sealed class Strategy : MutableEntity
{
    public Strategy(Guid id, string code, string displayName, DateTimeOffset createdAtUtc)
        : base(id, createdAtUtc)
    {
        Code = code;
        DisplayName = displayName;
    }

    public string Code { get; private set; }
    public string DisplayName { get; private set; }
    public bool IsEnabled { get; private set; }
}

public sealed class StrategyVersion : Entity, IAppendOnlyEntity
{
    public StrategyVersion(
        Guid id,
        Guid strategyId,
        string version,
        string definitionHash,
        DateTimeOffset createdAtUtc) : base(id)
    {
        StrategyId = strategyId;
        Version = version;
        DefinitionHash = definitionHash;
        CreatedAtUtc = createdAtUtc.ToUniversalTime();
    }

    public Guid StrategyId { get; private init; }
    public string Version { get; private init; }
    public string DefinitionHash { get; private init; }
    public DateTimeOffset CreatedAtUtc { get; private init; }
}

public sealed class StrategyConfiguration : MutableEntity
{
    public StrategyConfiguration(
        Guid id,
        TradingMode mode,
        Guid strategyVersionId,
        string parametersJson,
        DateTimeOffset createdAtUtc) : base(id, createdAtUtc)
    {
        Mode = mode;
        StrategyVersionId = strategyVersionId;
        ParametersJson = parametersJson;
    }

    public TradingMode Mode { get; private init; }
    public Guid StrategyVersionId { get; private init; }
    public string ParametersJson { get; private set; }
    public bool IsEnabled { get; private set; }
}

