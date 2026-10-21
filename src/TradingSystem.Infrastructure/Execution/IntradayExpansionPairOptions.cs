namespace TradingSystem.Infrastructure.Execution;

public sealed class IntradayExpansionPairOptions
{
    public const string SectionName = "PaperTrading:IntradayExpansionPair";
    public bool Enabled { get; init; } = true;
    public int ScanIntervalSeconds { get; init; } = 30;
    public string EntryWindowStart { get; init; } = "10:10";
    public string EntryCutoff { get; init; } = "14:30";
    public string ForcedExit { get; init; } = "15:10";
    public decimal TargetPremium { get; init; } = 60m;
    public decimal MinimumPremium { get; init; } = 30m;
    public decimal MaximumPremium { get; init; } = 100m;
    public decimal MaximumCombinedPremium { get; init; } = 170m;
    public decimal MaximumPremiumImbalance { get; init; } = 1.45m;
    public decimal MaximumSpreadPercent { get; init; } = 5m;
    public decimal BasketStopPercent { get; init; } = .28m;
    public decimal MaximumBasketRisk { get; init; } = 5000m;
    public decimal MinimumExpansionPercent { get; init; } = .10m;
    public decimal RunnerTrailFraction { get; init; } = .25m;
    public int MaximumLots { get; init; } = 2;
    public int MaximumPairsPerDay { get; init; } = 2;
    public int CooldownMinutes { get; init; } = 60;
    public int NearbyContractsPerSide { get; init; } = 10;
}
