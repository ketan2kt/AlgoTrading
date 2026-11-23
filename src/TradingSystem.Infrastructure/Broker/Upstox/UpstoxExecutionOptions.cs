namespace TradingSystem.Infrastructure.Broker.Upstox;

public sealed class UpstoxExecutionOptions
{
    public const string SectionName = "UpstoxExecution";
    // Configuration enables the adapter, never arms a market or moves an existing trade.
    public bool Enabled { get; set; }
    public bool Sandbox { get; set; }
    public string AccountId { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public string RedirectUri { get; set; } = "https://sarthico.com/api/upstox-execution/callback";
    public bool StaticIpVerified { get; set; }
    public string ApiBaseUrl { get; set; } = "https://relay.174-138-120-226.nip.io/upstox/";
}
