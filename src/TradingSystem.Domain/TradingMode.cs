namespace TradingSystem.Domain;

/// <summary>Server-authoritative operating mode.</summary>
public enum TradingMode
{
    Backtest = 0,
    Paper = 1,
    Live = 2
}

