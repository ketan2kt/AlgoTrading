namespace TradingSystem.Application.Execution;

public sealed record HistoricalFeedCaptureManifest(DateOnly Date, DateTimeOffset StartedAtUtc,
    DateTimeOffset? SubscribedAtUtc, DateTimeOffset? CompletedAtUtc,
    string UniverseFingerprint, int SampleSeconds, bool HasGaps,
    IReadOnlyList<HistoricalFeedInstrument> Instruments)
{
    public bool SensexPriorThesisStateCaptured { get; init; }
    public PersistentDirectionalThesisV1? PriorSensexThesis { get; init; }
    public SessionPolicyReceipt? PolicyReceipt { get; init; }
}

public static class HistoricalFeedCaptureEvidence
{
    public static bool CoversWindow(HistoricalFeedCaptureManifest manifest,
        DateOnly tradingDate, DateTimeOffset start, DateTimeOffset end) =>
        manifest.Date == tradingDate && !manifest.HasGaps && manifest.SampleSeconds is >= 1 and <= 5 &&
        manifest.StartedAtUtc <= start && manifest.SubscribedAtUtc <= start &&
        manifest.CompletedAtUtc >= end && manifest.Instruments.Count is > 0 and <= 1000;

    public static bool CoversNormalUniverse(HistoricalFeedCaptureManifest manifest, string market,
        IReadOnlyList<NiftyOptionContractCandidate> contracts, DateOnly tradingDate)
    {
        var eligible = contracts.Where(value => value.ExpiryDate > tradingDate).ToArray();
        if (eligible.Length == 0) return false;
        var expiry = eligible.Min(value => value.ExpiryDate);
        var expected = eligible.Where(value => value.ExpiryDate == expiry)
            .OrderBy(value => value.TradingSymbol, StringComparer.Ordinal).ToArray();
        var captured = manifest.Instruments.Where(value => value.Market == market &&
                value.Type is TradingSystem.Domain.Trading.InstrumentType.CallOption or TradingSystem.Domain.Trading.InstrumentType.PutOption)
            .OrderBy(value => value.TradingSymbol, StringComparer.Ordinal).ToArray();
        return expected.Length == captured.Length && expected.Zip(captured).All(pair =>
            pair.First.TradingSymbol == pair.Second.TradingSymbol && pair.First.Type == pair.Second.Type &&
            pair.First.ExpiryDate == pair.Second.ExpiryDate && pair.First.StrikePrice == pair.Second.StrikePrice &&
            pair.First.LotSize == pair.Second.LotSize && pair.First.TickSize == pair.Second.TickSize);
    }
}
