namespace TradingSystem.Application.Execution;

public sealed record TradingLotConfiguration(int NiftyLots, int SensexLots,
    DateTimeOffset? UpdatedAtUtc = null)
{
    public const int DefaultLots = 10;
    public const int MinimumLots = 1;
    public const int MaximumLots = 50;

    public static TradingLotConfiguration Default { get; } = new(DefaultLots, DefaultLots);

    public int ForMarket(string market) => market.Equals("sensex", StringComparison.OrdinalIgnoreCase)
        ? SensexLots : NiftyLots;

    public static bool IsValid(int lots) => lots is >= MinimumLots and <= MaximumLots;
}

public interface ITradingLotConfigurationService
{
    Task<TradingLotConfiguration> GetAsync(CancellationToken cancellationToken);
    Task<TradingLotConfiguration> SetAsync(int niftyLots, int sensexLots, string actor,
        CancellationToken cancellationToken);
}
