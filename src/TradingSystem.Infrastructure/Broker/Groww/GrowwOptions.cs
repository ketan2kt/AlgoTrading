namespace TradingSystem.Infrastructure.Broker.Groww;

public sealed class GrowwOptions
{
    public const string SectionName = "Groww";

    public string ApiBaseUrl { get; init; } = "https://api.groww.in/";
    public string ExecutionApiBaseUrl { get; init; } = "https://api.groww.in/";
    public string ExecutionRelayKeyEnvironmentVariable { get; init; } =
        "GROWW_EXECUTION_RELAY_KEY";
    public string TotpApiKeyEnvironmentVariable { get; init; } = "GROWW_TOTP_API_KEY";
    public string TotpSecretEnvironmentVariable { get; init; } = "GROWW_TOTP_SECRET";
    public string InstrumentMasterUrl { get; init; } =
        "https://growwapi-assets.groww.in/instruments/instrument.csv";
    public string AccessTokenEnvironmentVariable { get; init; } = "GROWW_ACCESS_TOKEN";
    public int TimeoutSeconds { get; init; } = 15;
    public int MaximumInstrumentBytes { get; init; } = 64 * 1024 * 1024;
}
