namespace TradingSystem.Application.Broker;

public static class GrowwQuoteTimestamp
{
    // Captured REST quotes include ten-digit Unix seconds; streaming quotes may
    // supply milliseconds. Preserve compatibility with either archived format.
    public static DateTimeOffset? ToUtc(long? epoch)
    {
        if (epoch is null or <= 0) return null;
        if (epoch < 100_000_000_000L) return DateTimeOffset.FromUnixTimeSeconds(epoch.Value);
        return epoch <= 253402300799999L ? DateTimeOffset.FromUnixTimeMilliseconds(epoch.Value) : null;
    }
}
