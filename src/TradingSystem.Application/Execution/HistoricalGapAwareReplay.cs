using TradingSystem.Domain.Trading;

namespace TradingSystem.Application.Execution;

public sealed record HistoricalReplayGap(DateTimeOffset StartUtc, DateTimeOffset EndUtc, string Reason);
public sealed record HistoricalGapAwareFill(HistoricalReplayFill Fill, string? ExclusionReason);
public sealed record HistoricalGapAwareWindow(DateTimeOffset StartUtc, DateTimeOffset EndUtc,
    bool IndependentFlatRestart, bool Simulated, int Candidates, int UnresolvedExecutions,
    IReadOnlyList<HistoricalGapAwareFill> Fills, decimal? VerifiedNetPnl, decimal? IndependentScenarioNetPnl);
public sealed record HistoricalGapAwareMarket(string Market, int ContractsWithFreshBooks, int FreshBookSamples,
    bool ArchiveReportedCaptureGaps, IReadOnlyList<HistoricalReplayGap> Gaps,
    IReadOnlyList<HistoricalGapAwareWindow> Windows, decimal? VerifiedNetPnl, IReadOnlyList<string> Limitations)
{
    public IReadOnlyList<string> ContractsWithoutFreshBooks { get; init; } = [];
}
public sealed record HistoricalGapAwareReport(DateOnly Date, IReadOnlyList<HistoricalGapAwareMarket> Markets);

public sealed class HistoricalGapAwareReplayService(IHistoricalReplayArchiveReader archive, IHistoricalPolicyBuildStore builds)
{
    public async Task<HistoricalGapAwareReport> RunAsync(DateOnly date, CancellationToken token)
    {
        var markets = new List<HistoricalGapAwareMarket>();
        foreach (var (key, label) in new[] { ("nifty", "NIFTY"), ("sensex", "SENSEX"), ("bank-nifty", "BANKNIFTY") })
        {
            token.ThrowIfCancellationRequested();
            HistoricalReplayArchiveSlice slice;
            try { slice = await archive.ReadGapAwareAsync(date, key, token); }
            catch (Exception error) when (error is InvalidDataException or InvalidOperationException or System.Text.Json.JsonException)
            {
                var start = new DateTimeOffset(date.ToDateTime(new(9, 15)), TimeSpan.FromHours(5.5)).ToUniversalTime();
                markets.Add(new(label, 0, 0, true, [new(start, start.AddMinutes(375), "Market archive unavailable or invalid")],
                    [], null, ["No verified P&L or simulation was produced for this market."]));
                continue;
            }
            var notes = new List<string>(slice.ControlStateGaps);
            if (slice.GapAwarePolicyInputsReady && slice.CapturedSessionPolicy is { } receipt && receipt.ValidFor(date))
            {
                try
                {
                    using var module = await builds.LoadAsync(receipt.BuildId, token);
                    var unsupported = ReplayContractCoverage.ValidateReplay(slice, receipt.Settings);
                    notes.AddRange(unsupported);
                    if (module.ContractSelectionVersion != RegularOptionContractSelector.Version)
                        notes.Add("The captured strategy build has an unsupported contract-selection version.");
                    else if (unsupported.Count == 0 && !receipt.Settings.WeeklyCarryForwardEnabled)
                    {
                        var captured = slice.RecordedContractUniverse!.Instruments.Where(value => value.Market == label)
                            .Select(value => value.TradingSymbol).ToHashSet(StringComparer.Ordinal);
                        markets.Add(HistoricalGapAwareReplay.Run(slice,
                            () => new HistoricalContractCoveragePolicy(module.Create(receipt.Settings, slice), label, date, captured),
                            notes, token));
                        continue;
                    }
                    else if (receipt.Settings.WeeklyCarryForwardEnabled) notes.Add("Overnight carry is not supported in this research mode.");
                }
                catch (Exception error) when (error is InvalidDataException or InvalidOperationException or ArgumentException)
                {
                    notes.Add("The captured build or its simulation inputs could not be verified. No current-build substitute was used.");
                }
            }
            else notes.Add("Required dated policy, controls, contract plan, or market anchors were not verified.");
            markets.Add(HistoricalGapAwareReplay.Run(slice, null, notes, token));
        }
        return new(date, markets);
    }
}

// Separate research mode. The strict full-session comparison and its admission
// gates are never relaxed. A known loss of state creates an explicit flat reset,
// not an assumed exit or continuation.
public static class HistoricalGapAwareReplay
{
    private static readonly TimeSpan MaximumSourceAge = TimeSpan.FromSeconds(30);
    private const int MaxQuotesPerMarket = 2_000_000;
    private const int MaxSimulatedWindows = 32;

    public static HistoricalGapAwareMarket Run(HistoricalReplayArchiveSlice slice,
        Func<IHistoricalReplayPortfolioPolicy>? createPolicy,
        IReadOnlyList<string>? limitations = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(slice);
        var session = slice.Session;
        if (session.IndexSymbol is not ("NIFTY" or "SENSEX" or "BANKNIFTY") ||
            session.StartUtc >= session.EndUtc || session.EndUtc - session.StartUtc > TimeSpan.FromHours(7))
            throw new ArgumentException("A single bounded index session is required.", nameof(slice));
        if (session.Quotes.Count > MaxQuotesPerMarket)
            throw new InvalidDataException("Gap-aware market archive exceeds its one-session quote budget.");

        var eligible = slice.RecordedContractUniverse?.Instruments.Where(value => value.Market == session.IndexSymbol &&
                value.Type is InstrumentType.CallOption or InstrumentType.PutOption)
            .Select(value => value.TradingSymbol).ToHashSet(StringComparer.Ordinal) ??
            slice.OptionContracts.Select(value => value.TradingSymbol).ToHashSet(StringComparer.Ordinal);
        eligible.Remove(session.IndexSymbol);
        if (slice.FrontFutureSymbol is { } future) eligible.Remove(future);
        var source = new List<HistoricalReplayQuote>();
        var books = new Dictionary<string, List<HistoricalReplayQuote>>(StringComparer.Ordinal);
        var coveredContracts = new HashSet<string>(StringComparer.Ordinal);
        var sampling = TimeSpan.FromSeconds(3 * Math.Clamp(slice.CaptureSampleSeconds, 1, 5));
        var observed = new List<DateTimeOffset>();
        var count = 0;
        foreach (var quote in HistoricalQuotePaths.Ordered(session.Quotes))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (quote.Source != "GrowwFeedSample" || quote.AvailableAtUtc < session.StartUtc ||
                quote.AvailableAtUtc > session.EndUtc || !IsFresh(quote.SourceAtUtc, quote.AvailableAtUtc))
                continue;
            if (eligible.Contains(quote.Symbol))
            {
                if (!IsFresh(quote.DepthSourceAtUtc, quote.AvailableAtUtc) || !quote.HasRecordedBookEvidence()) continue;
                if (HasPositiveDepth(quote))
                {
                    if (observed.Count == 0 || observed[^1] != quote.AvailableAtUtc) observed.Add(quote.AvailableAtUtc);
                    coveredContracts.Add(quote.Symbol);
                    if (createPolicy is not null)
                    {
                        if (!books.TryGetValue(quote.Symbol, out var path)) books.Add(quote.Symbol, path = []);
                        path.Add(quote);
                    }
                    count++;
                }
                if (createPolicy is not null) source.Add(quote);
            }
            else if (createPolicy is not null && quote.Symbol == session.IndexSymbol) source.Add(quote);
        }

        var gaps = slice.UnreadableQuoteIntervals.ToList();
        var previous = session.StartUtc;
        foreach (var next in observed.Append(session.EndUtc))
        {
            if (next - previous > sampling) gaps.Add(new(previous, next, "Missing fresh two-sided option samples"));
            previous = next;
        }
        if (createPolicy is not null)
        {
            AddMissingBars(session.IndexSymbol);
            if (session.IndexSymbol != "BANKNIFTY" && slice.FrontFutureSymbol is { } symbol) AddMissingBars(symbol);
        }
        var mergedGaps = Merge(gaps, session.StartUtc, session.EndUtc);
        var windows = new List<HistoricalGapAwareWindow>();
        var notes = (limitations ?? []).ToList();
        if (createPolicy is null) notes.Add("Policy inputs not verified; this market is shown as coverage only.");
        notes.Add("Verified P&L includes only complete evidence-backed simulated positions before any loss of session state. Costs and slippage remain modeled.");
        notes.Add("After a gap, each window starts flat as a separate scenario. Its results are not verified full-day P&L.");
        if (slice.ArchiveReportedCaptureGaps)
            notes.Add("The archive reported capture loss; displayed intervals come from this market's recorded quote timestamps and known unreadable files.");
        var from = session.StartUtc;
        var windowCount = 0;
        foreach (var gap in mergedGaps.Append(new HistoricalReplayGap(session.EndUtc, session.EndUtc, "")))
        {
            if (gap.StartUtc > from)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var reset = from != session.StartUtc;
                var until = gap.StartUtc;
                if (createPolicy is null || windowCount++ >= MaxSimulatedWindows)
                    windows.Add(new(from, until, reset, false, 0, 0, [], null, null));
                else
                {
                    var observedQuotes = source.Where(q => q.AvailableAtUtc >= from && q.AvailableAtUtc < until).ToArray();
                    var result = HistoricalPortfolioReplay.Run(session.Bars, observedQuotes, createPolicy(),
                        SessionPolicyReceipt.ComparisonExecution, from, until,
                        session.OptionChains, session.ExchangeCode, sourceControls: session.ControlEvents,
                        cancellationToken: cancellationToken);
                    windows.Add(ScoreWindow(from, until, reset, result, books));
                }
            }
            from = gap.EndUtc;
        }
        if (windowCount > MaxSimulatedWindows) notes.Add("Additional fragmented windows are shown as coverage only (simulation budget).");
        var verified = windows.Where(value => value.VerifiedNetPnl.HasValue).ToArray();
        return new(session.IndexSymbol, coveredContracts.Count, count, slice.ArchiveReportedCaptureGaps, mergedGaps, windows,
            verified.Length == 0 ? null : verified.Sum(value => value.VerifiedNetPnl), notes)
        {
            ContractsWithoutFreshBooks = eligible.Except(coveredContracts, StringComparer.Ordinal)
                .Order(StringComparer.Ordinal).ToArray()
        };

        void AddMissingBars(string symbol)
        {
            var minutes = session.Bars.Where(bar => bar.Symbol == symbol && bar.IntervalSeconds == 60)
                .Select(bar => bar.OpenTimeUtc).ToHashSet();
            for (var minute = session.StartUtc; minute.AddMinutes(1) <= session.EndUtc; minute = minute.AddMinutes(1))
                if (!minutes.Contains(minute)) gaps.Add(new(minute, minute.AddMinutes(1), symbol + " minute bar unavailable"));
        }
    }

    private static bool IsFresh(DateTimeOffset? source, DateTimeOffset at) =>
        source is { } timestamp && timestamp <= at && at - timestamp <= MaximumSourceAge;
    private static bool HasPositiveDepth(HistoricalReplayQuote quote) =>
        quote.HasExecutableBook() && quote.BidQuantity > 0 && quote.OfferQuantity > 0;

    private static HistoricalReplayGap[] Merge(IEnumerable<HistoricalReplayGap> source,
        DateTimeOffset start, DateTimeOffset end)
    {
        var merged = new List<HistoricalReplayGap>();
        foreach (var gap in source.Where(value => value.EndUtc > start && value.StartUtc < end && value.EndUtc > value.StartUtc)
            .Select(value => value with { StartUtc = value.StartUtc < start ? start : value.StartUtc,
                EndUtc = value.EndUtc > end ? end : value.EndUtc })
            .OrderBy(value => value.StartUtc).ThenBy(value => value.EndUtc))
        {
            if (merged.Count == 0 || merged[^1].EndUtc < gap.StartUtc) { merged.Add(gap); continue; }
            var prior = merged[^1];
            merged[^1] = prior with { EndUtc = gap.EndUtc > prior.EndUtc ? gap.EndUtc : prior.EndUtc,
                Reason = string.Join("; ", prior.Reason.Split("; ", StringSplitOptions.RemoveEmptyEntries)
                    .Append(gap.Reason).Distinct(StringComparer.Ordinal)) };
        }
        return merged.ToArray();
    }

    private static HistoricalGapAwareWindow ScoreWindow(DateTimeOffset start, DateTimeOffset end, bool reset,
        HistoricalReplayResult result, Dictionary<string, List<HistoricalReplayQuote>> books)
    {
        var groups = result.Trades.GroupBy(fill => (fill.Symbol, fill.DecisionAtUtc, fill.EntryAtUtc, fill.Strategy))
            .Select(group => (Fills: group.ToArray(), Reason: Exclusion(group.ToArray()))).ToArray();
        var firstUncertainty = groups.Where(group => group.Reason is not null)
            .Select(group => (DateTimeOffset?)group.Fills[0].DecisionAtUtc)
            .Append(result.FirstExecutionUncertaintyAtUtc).Min();
        var fills = groups.SelectMany(group =>
        {
            var reason = group.Reason ?? (firstUncertainty is { } unknown &&
                group.Fills.Any(fill => fill.ExitAtUtc >= unknown)
                ? "Earlier execution uncertainty makes the position state unverifiable." : null);
            return group.Fills.Select(fill => new HistoricalGapAwareFill(fill, reason));
        }).ToArray();
        var scored = fills.Where(value => value.ExclusionReason is null).ToArray();
        var net = scored.Length == 0 ? (decimal?)null : scored.Sum(value => value.Fill.NetPnl);
        return new(start, end, reset, true, result.CandidateCount,
            result.MissingEntryQuoteCount + result.MissingExitQuoteCount + result.MissingAddOnQuoteCount +
            result.UnsupportedPartialCount + groups.Count(group => group.Reason is not null), fills,
            reset ? null : net, reset ? net : null);

        string? Exclusion(HistoricalReplayFill[] position)
        {
            var first = position[0];
            if (position.Count(fill => !fill.IsPartialExit) != 1)
                return "Position did not close with evidence before the window ended.";
            if (!books.TryGetValue(first.Symbol, out var path))
                return "No fresh two-sided book was recorded for this contract.";
            var lastExit = position.Max(fill => fill.ExitAtUtc);
            var begin = LowerBound(path, first.DecisionAtUtc);
            if (begin > 0 && path[begin - 1].AvailableAtUtc >= start) begin--;
            var last = first.DecisionAtUtc;
            for (var i = begin; i < path.Count && path[i].AvailableAtUtc <= lastExit; i++)
            {
                if (path[i].AvailableAtUtc - last > MaximumSourceAge)
                    return "Selected contract lacked a fresh continuous quote path.";
                last = path[i].AvailableAtUtc;
            }
            if (lastExit - last > MaximumSourceAge)
                return "Selected contract lacked a fresh continuous quote path.";
            foreach (var fill in position)
            {
                if (fill.EntryPriceSource != "ObservedOffer" || fill.ExitPriceSource != "ObservedBid" ||
                    !HasSize(path, fill.EntryAtUtc, fill.EntryUnits > 0 ? fill.EntryUnits : fill.OriginalQuantity, buy: true) ||
                    !HasSize(path, fill.ExitAtUtc, fill.Quantity, buy: false) ||
                    fill.AddOnAt is { } addAt && !HasSize(path, addAt, fill.AddOnUnits, buy: true))
                    return "One or more fills lacked a fresh recorded bid/offer with enough displayed size.";
            }
            return null;
        }
    }

    private static bool HasSize(List<HistoricalReplayQuote> path, DateTimeOffset time, int quantity, bool buy)
    {
        var i = LowerBound(path, time);
        return i < path.Count && path[i].AvailableAtUtc == time && quantity > 0 &&
            (buy ? path[i].OfferQuantity : path[i].BidQuantity) >= quantity;
    }

    private static int LowerBound(List<HistoricalReplayQuote> path, DateTimeOffset time)
    {
        var low = 0;
        var high = path.Count;
        while (low < high)
        {
            var mid = low + (high - low) / 2;
            if (path[mid].AvailableAtUtc < time) low = mid + 1; else high = mid;
        }
        return low;
    }
}
