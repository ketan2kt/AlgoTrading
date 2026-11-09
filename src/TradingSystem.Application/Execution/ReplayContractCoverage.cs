using TradingSystem.Domain.Trading;

namespace TradingSystem.Application.Execution;

public sealed record ReplayRecordedContractUniverse(string SelectionVersion,
    IReadOnlyList<HistoricalFeedInstrument> Instruments);
public sealed record ReplayMarketContractCoverage(string Market, decimal Anchor,
    decimal? MinimumStrike, decimal? MaximumStrike, IReadOnlyList<DateOnly> Expiries,
    int OptionContracts, IReadOnlyList<string> Issues)
{
    public decimal? SupportedUnderlyingLow { get; init; }
    public decimal? SupportedUnderlyingHigh { get; init; }
}
public sealed record ReplayContractPreflight(DateOnly Date, DateTimeOffset CheckedAtUtc,
    string SelectionVersion, string? PolicyFingerprint, int Instruments, int Subjects,
    IReadOnlyList<ReplayMarketContractCoverage> Markets, IReadOnlyList<string> Issues)
{
    public bool Ready => Issues.Count == 0 && Markets.Count == 3 && Markets.All(m => m.Issues.Count == 0);
}
public sealed record ReplayContractCoverageInput(IReadOnlyList<NiftyOptionContractCandidate> Contracts,
    IReadOnlyDictionary<string, decimal> Anchors);

public static class ReplayContractCoverage
{
    public static NiftyOptionContractCandidate[] Contracts(IEnumerable<HistoricalFeedInstrument> rows) => rows
        .Where(c => c.Type is InstrumentType.CallOption or InstrumentType.PutOption && c.ExpiryDate.HasValue && c.StrikePrice.HasValue)
        .Select(c => new NiftyOptionContractCandidate(Guid.Empty, c.TradingSymbol, c.Type,
            c.ExpiryDate!.Value, c.StrikePrice!.Value, c.LotSize, c.TickSize)).ToArray();

    public static ReplayContractPreflight Preflight(DateOnly date, DateTimeOffset at,
        IReadOnlyList<HistoricalFeedInstrument> plan, ReplayContractCoverageInput input, SessionPolicyReceipt? receipt)
    {
        var issues = new List<string>();
        var open = new DateTimeOffset(date.ToDateTime(new TimeOnly(9, 15)), TimeSpan.FromHours(5.5));
        if (at >= open) issues.Add("Contract coverage was not verified before 09:15 IST.");
        if (receipt?.ValidFor(date) != true) issues.Add("Verified pre-open website policy receipt missing.");
        var subjects = plan.Sum(c => c.Type == InstrumentType.Index ? 1 : 2);
        if (plan.Count > 500 || subjects > 1000) issues.Add("Contract plan exceeds the broker subscription budget.");
        var markets = RegularOptionContractSelector.Markets.Select(market =>
        {
            var captured = plan.Where(c => c.Market == market && c.Type is InstrumentType.CallOption or InstrumentType.PutOption).ToArray();
            var problems = new List<string>();
            var anchor = input.Anchors.GetValueOrDefault(market);
            var minimum = captured.Select(c => c.StrikePrice).Min();
            var maximum = captured.Select(c => c.StrikePrice).Max();
            var settings = receipt?.Settings.NiftySelection ?? new();
            var padding = market == "NIFTY"
                ? (Math.Max(Math.Abs(settings.NormalInTheMoneySteps), 1) + 1) * settings.StrikeStep
                : market == "SENSEX" ? 200m : 100m;
            var lower = minimum + padding;
            var upper = maximum - padding;
            if (anchor <= 0) problems.Add("Pre-session price anchor missing.");
            else if (receipt is not null)
            {
                problems.AddRange(CheckRange(input.Contracts, captured, market, date, anchor, anchor, receipt.Settings.NiftySelection));
                if (lower is > 0 && upper >= lower)
                    problems.AddRange(CheckRange(input.Contracts, captured, market, date, lower.Value, upper!.Value, settings));
                else problems.Add("Recorded band has no guarded underlying-price interval.");
                if (anchor < lower || anchor > upper) problems.Add("Pre-session anchor is outside the guarded recorded price interval.");
            }
            if (captured.Length == 0) problems.Add("No option contracts planned.");
            return new ReplayMarketContractCoverage(market, anchor,
                minimum, maximum,
                captured.Where(c => c.ExpiryDate.HasValue).Select(c => c.ExpiryDate!.Value).Distinct().Order().ToArray(),
                captured.Length, problems) { SupportedUnderlyingLow = lower, SupportedUnderlyingHigh = upper };
        }).ToArray();
        return new(date, at.ToUniversalTime(), RegularOptionContractSelector.Version,
            receipt?.Fingerprint, plan.Count, subjects, markets, issues);
    }

    // Validate BOTH versions against the same observed price envelope, independently
    // of their accepted entries. Future bars are used only to certify data coverage;
    // none of this envelope is passed to a strategy's as-of decision frame.
    public static IReadOnlyList<string> ValidateReplay(HistoricalReplayArchiveSlice slice,
        HistoricalNormalPolicyConfiguration configuration)
    {
        var universe = slice.RecordedContractUniverse;
        if (universe is null) return ["Recorded contract-universe manifest missing; unsupported replay."];
        if (universe.SelectionVersion != RegularOptionContractSelector.Version)
            return ["Recorded contract-selection version is unsupported; no substitute universe was used."];
        var date = slice.Session.Date;
        var market = slice.Session.IndexSymbol;
        var bars = slice.Session.Bars.Where(b => b.Symbol == market &&
            DateOnly.FromDateTime(b.OpenTimeUtc.ToOffset(TimeSpan.FromHours(5.5)).DateTime) == date).ToArray();
        if (bars.Length == 0) return ["Session price envelope unavailable for contract coverage verification."];
        return CheckRange(slice.OptionContracts, universe.Instruments.Where(c => c.Market == market).ToArray(),
            market, date, bars.Min(b => b.Low), bars.Max(b => b.High), configuration.NiftySelection);
    }

    public static IReadOnlyList<string> CheckRange(IReadOnlyCollection<NiftyOptionContractCandidate> master,
        IReadOnlyList<HistoricalFeedInstrument> captured, string market, DateOnly date,
        decimal low, decimal high, NiftyOptionSelectionOptions nifty)
    {
        IReadOnlyList<NiftyOptionContractCandidate> required;
        try { required = RegularOptionContractSelector.RequiredForRange(master, market, date, low, high, nifty); }
        catch (InvalidOperationException ex) { return [ex.Message]; }
        var bySymbol = captured.GroupBy(c => c.TradingSymbol, StringComparer.Ordinal)
            .Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single(), StringComparer.Ordinal);
        var absent = required.Where(c => !bySymbol.TryGetValue(c.TradingSymbol, out var saved) ||
            saved.Type != c.Type || saved.ExpiryDate != c.ExpiryDate || saved.StrikePrice != c.StrikePrice ||
            saved.LotSize != c.LotSize || saved.TickSize != c.TickSize).ToArray();
        return absent.Length == 0 ? [] :
            [$"Unsupported contract coverage: {market} requires {absent.Length} unrecorded/mismatched contracts " +
             $"(including {string.Join(", ", absent.Take(3).Select(c => c.TradingSymbol))}). " +
             "Strike/expiry or selection settings exceed the saved universe; no trades were simulated for this date."];
    }
}
