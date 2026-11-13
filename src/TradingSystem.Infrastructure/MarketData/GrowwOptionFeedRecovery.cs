namespace TradingSystem.Infrastructure.MarketData;

// One instance per market and option channel; only the timer calls TryStartRenewal.
internal sealed class GrowwOptionFeedRecovery
{
    private static readonly TimeSpan India = TimeSpan.FromHours(5.5);
    private static readonly TimeSpan Silence = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan MinimumRenewalInterval = TimeSpan.FromSeconds(30);
    private long latestSourceTicks;
    private long lastRenewedSource = -1;
    private DateTimeOffset? lastRenewal;

    public void Observe(DateTimeOffset source, DateTimeOffset received)
    {
        if (source > received || received - source > TimeSpan.FromSeconds(30)) return;
        var ticks = source.UtcTicks;
        var previous = Interlocked.Read(ref latestSourceTicks);
        while (ticks > previous)
        {
            var current = Interlocked.CompareExchange(ref latestSourceTicks, ticks, previous);
            if (current == previous) break;
            previous = current;
        }
    }

    public bool TryStartRenewal(DateTimeOffset subscribed, DateTimeOffset now)
    {
        var local = now.ToOffset(India);
        var open = new DateTimeOffset(local.Date.AddHours(9).AddMinutes(15), India);
        if (now < open || TimeOnly.FromDateTime(local.DateTime) >= new TimeOnly(15, 30)) return false;
        var source = Interlocked.Read(ref latestSourceTicks);
        var started = Math.Max(open.UtcTicks, subscribed.UtcTicks);
        if (now.UtcTicks - Math.Max(started, source) < Silence.Ticks ||
            source == lastRenewedSource || (lastRenewal is { } previous && now - previous < MinimumRenewalInterval)) return false;
        lastRenewedSource = source;
        lastRenewal = now;
        return true;
    }
}
