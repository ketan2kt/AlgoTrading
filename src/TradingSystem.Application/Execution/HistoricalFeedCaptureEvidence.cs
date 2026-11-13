namespace TradingSystem.Application.Execution;

public sealed record HistoricalFeedCaptureManifest(DateOnly Date, DateTimeOffset StartedAtUtc,
    DateTimeOffset? SubscribedAtUtc, DateTimeOffset? CompletedAtUtc,
    string UniverseFingerprint, int SampleSeconds, bool HasGaps,
    IReadOnlyList<HistoricalFeedInstrument> Instruments)
{
    public string UniverseScope { get; init; } = "FullSelectedExpiry";
    public DateTimeOffset? LastCheckpointAtUtc { get; init; }
    public bool SensexPriorThesisStateCaptured { get; init; }
    public PersistentDirectionalThesisV1? PriorSensexThesis { get; init; }
    public SessionPolicyReceipt? PolicyReceipt { get; init; }
    public string? ContractSelectionVersion { get; init; }
    public ReplayContractPreflight? ContractPreflight { get; init; }
    public IReadOnlyList<string> ContractCoverageIssues { get; init; } = [];
    public string? InterruptionReason { get; init; }
    public HistoricalFeedFrameRejection? LastRejectedFrame { get; init; }
    public IReadOnlyList<HistoricalDepthMarketAudit> OptionDepthAudit { get; init; } = [];
}

public sealed record HistoricalDepthGap(DateTimeOffset StartUtc, DateTimeOffset EndUtc);

// Market-level continuity, not a guarantee that every individual strike was liquid.
public sealed record HistoricalDepthMarketAudit(string Market, DateTimeOffset? LastReceiptUtc,
    DateTimeOffset? LastSourceUtc, IReadOnlyList<HistoricalDepthGap> Gaps);

public sealed record HistoricalFeedFrameRejection(DateTimeOffset AtUtc, string Market, string TradingSymbol, string Channel);

public static class HistoricalFeedCaptureEvidence
{
    public static bool CoversWindow(HistoricalFeedCaptureManifest manifest,
        DateOnly tradingDate, DateTimeOffset start, DateTimeOffset end) =>
        manifest.Date == tradingDate && !manifest.HasGaps && manifest.SampleSeconds is >= 1 and <= 5 &&
        manifest.StartedAtUtc <= start && manifest.SubscribedAtUtc <= start &&
        manifest.CompletedAtUtc >= end && manifest.Instruments.Count is > 0 and <= 1000;

    public static bool CoversNormalUniverse(HistoricalFeedCaptureManifest manifest, string market,
        IReadOnlyList<NiftyOptionContractCandidate> contracts, DateOnly tradingDate,
        IReadOnlyList<HistoricalReplayBar>? sessionBars = null, NiftyOptionSelectionOptions? niftySelection = null)
    {
        var eligible = RegularOptionContractSelector.Eligible(contracts.ToArray(), market, tradingDate,
                TradingSystem.Domain.Trading.Direction.Buy, niftySelection)
            .Concat(RegularOptionContractSelector.Eligible(contracts.ToArray(), market, tradingDate,
                TradingSystem.Domain.Trading.Direction.Sell, niftySelection)).ToArray();
        if (eligible.Length == 0) return false;
        var expected = eligible
            .OrderBy(value => value.TradingSymbol, StringComparer.Ordinal).ToArray();
        var captured = manifest.Instruments.Where(value => value.Market == market &&
                value.Type is TradingSystem.Domain.Trading.InstrumentType.CallOption or TradingSystem.Domain.Trading.InstrumentType.PutOption)
            .OrderBy(value => value.TradingSymbol, StringComparer.Ordinal).ToArray();
        if (manifest.UniverseScope == "PreSessionStrikeBand")
        {
            if (captured.Length == 0 || sessionBars is null) return false;
            var bars = sessionBars.Where(bar => bar.Symbol == market &&
                DateOnly.FromDateTime(bar.OpenTimeUtc.ToOffset(TimeSpan.FromHours(5.5)).DateTime) == tradingDate).ToArray();
            if (bars.Length == 0 || captured.Any(value => value.StrikePrice is null)) return false;
            var lower = captured.Min(value => value.StrikePrice!.Value);
            var upper = captured.Max(value => value.StrikePrice!.Value);
            // Require a guard strike on each side of the entire observed path.
            // Never certify a session that moved outside the preselected band.
            if (bars.Min(value => value.Low) <= lower || bars.Max(value => value.High) >= upper) return false;
            expected = expected.Where(value => value.StrikePrice >= lower && value.StrikePrice <= upper).ToArray();
        }
        else if (manifest.UniverseScope != "FullSelectedExpiry") return false;
        return expected.Length == captured.Length && expected.Zip(captured).All(pair =>
            pair.First.TradingSymbol == pair.Second.TradingSymbol && pair.First.Type == pair.Second.Type &&
            pair.First.ExpiryDate == pair.Second.ExpiryDate && pair.First.StrikePrice == pair.Second.StrikePrice &&
            pair.First.LotSize == pair.Second.LotSize && pair.First.TickSize == pair.Second.TickSize);
    }
}
