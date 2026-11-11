using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using TradingSystem.Application.Broker;
using TradingSystem.Application.Execution;
using TradingSystem.Domain.Trading;
using TradingSystem.Infrastructure.Persistence;
using TradingSystem.Infrastructure.MarketData;

namespace TradingSystem.Infrastructure.Execution;

// Read-only historical input assembly. It never falls back to the original
// day's decisions or orders when a required market series is absent.
internal sealed class EfHistoricalReplayArchiveReader(TradingDbContext db, IHistoricalQuoteArchive? quoteArchive = null,
    FileHistoricalFeedManifestReader? feedManifests = null)
    : IHistoricalReplayArchiveReader
{
    private static readonly TimeSpan IndiaOffset = TimeSpan.FromHours(5.5);

    public Task<HistoricalReplayArchiveSlice> ReadAsync(DateOnly tradingDate, string market,
        CancellationToken cancellationToken) => ReadCoreAsync(tradingDate, market, false, cancellationToken);

    public Task<HistoricalReplayArchiveSlice> ReadGapAwareAsync(DateOnly tradingDate, string market,
        CancellationToken cancellationToken) => ReadCoreAsync(tradingDate, market, true, cancellationToken);

    private async Task<HistoricalReplayArchiveSlice> ReadCoreAsync(DateOnly tradingDate, string market,
        bool gapAware, CancellationToken cancellationToken)
    {
        var (exchange, underlying) = market switch
        {
            "nifty" => ("NSE", "NIFTY"),
            "sensex" => ("BSE", "SENSEX"),
            "bank-nifty" => ("NSE", "BANKNIFTY"),
            _ => throw new ArgumentOutOfRangeException(nameof(market))
        };
        var start = new DateTimeOffset(tradingDate.ToDateTime(new TimeOnly(9, 15)),
            IndiaOffset).ToUniversalTime();
        var end = new DateTimeOffset(tradingDate.ToDateTime(new TimeOnly(15, 30)),
            IndiaOffset).ToUniversalTime();
        var controls = await new EfHistoricalReplayControlReader(db).ReadAsync(market, start, end, cancellationToken);
        var feedManifest = feedManifests is null ? null :
            await feedManifests.ReadEvidenceAsync(tradingDate, start, end, cancellationToken);
        var feedWindowComplete = feedManifest is not null &&
            HistoricalFeedCaptureEvidence.CoversWindow(feedManifest, tradingDate, start, end);
        var datedMaster = await db.HistoricalInstrumentMasterSnapshots.AsNoTracking()
            .Where(value => value.SessionDate == tradingDate &&
                value.Source == "GrowwInstrumentMasterV1" &&
                value.CapturedAtUtc <= start)
            .OrderByDescending(value => value.CapturedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);
        HistoricalInstrumentSnapshotItem[]? datedItems = null;
        if (datedMaster is not null)
        {
            try
            {
                datedItems = JsonSerializer.Deserialize<HistoricalInstrumentSnapshotItem[]>(
                    datedMaster.PayloadJson);
            }
            catch (JsonException exception)
            {
                throw new InvalidOperationException(
                    "The dated instrument-master snapshot cannot be parsed.", exception);
            }
            var verified = HistoricalInstrumentMasterFingerprint.Create(datedItems ?? []);
            if (verified.Fingerprint != datedMaster.Fingerprint ||
                verified.ContractCount != datedMaster.ContractCount)
                throw new InvalidOperationException(
                    "The dated instrument-master snapshot fingerprint does not match its payload.");
        }
        var index = await db.Instruments.AsNoTracking().Where(value =>
                value.Exchange == exchange && value.Segment == InstrumentSegment.Cash &&
                value.Type == InstrumentType.Index &&
                value.TradingSymbol == underlying)
            .Select(value => new { value.Id, value.TradingSymbol, value.GrowwSymbol })
            .SingleOrDefaultAsync(cancellationToken);
        if (index is null)
            throw new InvalidOperationException("No archived index instrument exists.");
        var future = await db.Instruments.AsNoTracking().Where(value =>
                value.Exchange == exchange &&
                value.Segment == InstrumentSegment.FuturesAndOptions &&
                value.Type == InstrumentType.Future &&
                value.TradingSymbol.StartsWith(underlying) &&
                value.ExpiryDate >= tradingDate &&
                value.ExpiryDate <= tradingDate.AddDays(60))
            .OrderBy(value => value.ExpiryDate).ThenBy(value => value.TradingSymbol)
            .Select(value => new { value.Id, value.TradingSymbol, value.GrowwSymbol, value.ExpiryDate })
            .FirstOrDefaultAsync(cancellationToken);
        var optionInstruments = await db.Instruments.AsNoTracking().Where(value =>
                value.Exchange == exchange &&
                value.Segment == InstrumentSegment.FuturesAndOptions &&
                value.TradingSymbol.StartsWith(underlying) &&
                (value.Type == InstrumentType.CallOption ||
                 value.Type == InstrumentType.PutOption) &&
                value.ExpiryDate >= tradingDate &&
                value.ExpiryDate <= tradingDate.AddDays(60))
            .Select(value => new { value.Id, value.TradingSymbol, value.GrowwSymbol,
                value.Type,
                value.ExpiryDate, value.StrikePrice, value.LotSize, value.TickSize })
            .ToArrayAsync(cancellationToken);
        var mappedProviderSymbols = optionInstruments.Where(value =>
                !string.IsNullOrWhiteSpace(value.GrowwSymbol))
            .GroupBy(value => value.GrowwSymbol!, StringComparer.Ordinal)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key,
                group => group.Single().TradingSymbol, StringComparer.Ordinal);
        var optionIds = optionInstruments.Select(value => value.Id).ToArray();
        var pageReceipts = await db.HistoricalUniversePageReceipts.AsNoTracking()
            .Where(value => value.SessionDate == tradingDate && value.Market == market)
            .OrderBy(value => value.ExpiryDate).ThenBy(value => value.Page)
            .ToArrayAsync(cancellationToken);
        var ids = optionIds.Append(index.Id)
            .Concat(future is null ? [] : [future.Id]).ToArray();
        var candles = await db.Candles.AsNoTracking().Where(value =>
                ids.Contains(value.InstrumentId) && value.Source == "Groww" &&
                value.IntervalSeconds == 60 && value.OpenTimeUtc >= start &&
                value.OpenTimeUtc < end)
            .Select(value => new { value.InstrumentId, value.OpenTimeUtc,
                value.Open, value.High, value.Low, value.Close, value.Volume,
                value.OpenInterest })
            .ToArrayAsync(cancellationToken);
        var unmappedPaths = await db.HistoricalResearchCandles.AsNoTracking()
            .Where(value => value.Exchange == exchange &&
                value.Underlying == underlying && (value.Source == "GrowwHistoricalApiV1" || value.Source == GrowwHistoricalResearchImporter.IndexSource) &&
                value.IntervalSeconds == 60 && value.OpenTimeUtc >= start &&
                value.OpenTimeUtc < end)
            .Select(value => new { value.GrowwSymbol, value.ExpiryDate, value.Source,
                value.OpenTimeUtc, value.Open, value.High, value.Low,
                value.Close, value.Volume, value.OpenInterest })
            .ToArrayAsync(cancellationToken);
        var rawFront = unmappedPaths.Where(value =>
                value.GrowwSymbol.EndsWith("-FUT", StringComparison.Ordinal) &&
                value.ExpiryDate >= tradingDate &&
                value.ExpiryDate <= tradingDate.AddDays(60))
            .OrderBy(value => value.ExpiryDate)
            .ThenBy(value => value.GrowwSymbol, StringComparer.Ordinal)
            .FirstOrDefault();
        var useRawFuture = rawFront is not null &&
            (future is null || rawFront.ExpiryDate < future.ExpiryDate);
        var frontFutureSymbol = useRawFuture ? rawFront!.GrowwSymbol :
            future?.TradingSymbol;
        var priorStart = start.AddDays(-21);
        var chains = await db.OptionChainSnapshots.AsNoTracking().Where(value =>
                value.UnderlyingInstrumentId == index.Id &&
                value.ReceivedAtUtc >= start.AddMinutes(-3) &&
                value.ReceivedAtUtc < end && value.IsValid)
            .OrderBy(value => value.ReceivedAtUtc)
            .Select(value => new { value.ReceivedAtUtc, value.Source,
                value.SourceTimestampUtc, value.ExpiryDate, value.PayloadJson })
            .ToArrayAsync(cancellationToken);
        var prior = await db.Candles.AsNoTracking().Where(value =>
                value.InstrumentId == index.Id && value.Source == "Groww" &&
                value.IntervalSeconds == 60 && value.OpenTimeUtc >= priorStart &&
                value.OpenTimeUtc < start)
            .Select(value => new { value.OpenTimeUtc, value.Open, value.High,
                value.Low, value.Close, value.Volume, value.OpenInterest })
            .ToArrayAsync(cancellationToken);
        var repairedPrior = await db.HistoricalResearchCandles.AsNoTracking().Where(value =>
                value.Exchange == exchange && value.Underlying == underlying &&
                value.Source == GrowwHistoricalResearchImporter.IndexSource &&
                value.GrowwSymbol == index.GrowwSymbol && value.IntervalSeconds == 60 &&
                value.OpenTimeUtc >= priorStart && value.OpenTimeUtc < start)
            .Select(value => new { value.OpenTimeUtc, value.Open, value.High, value.Low, value.Close, value.Volume, value.OpenInterest })
            .ToArrayAsync(cancellationToken);
        var recordedPriorTimes = prior.Select(value => value.OpenTimeUtc).ToHashSet();
        prior = prior.Concat(repairedPrior.Where(value => !recordedPriorTimes.Contains(value.OpenTimeUtc)))
            .OrderBy(value => value.OpenTimeUtc).ToArray();
        var previousSessions = prior.GroupBy(value => DateOnly.FromDateTime(
                value.OpenTimeUtc.ToOffset(IndiaOffset).DateTime))
            .OrderByDescending(value => value.Key).Take(5).ToArray();
        // A partial previous session cannot provide an honest previous close.
        // Keep its bars visible for diagnostics, but fail the Nifty signal
        // adapter closed until a complete prior day is archived.
        var previousClose = previousSessions.Length == 0 ||
            !HistoricalSessionCoverageEvaluator.Evaluate(previousSessions[0].Key,
                previousSessions[0].Select(value => value.OpenTimeUtc)).IsComplete
            ? (decimal?)null
            : previousSessions[0].OrderByDescending(value => value.OpenTimeUtc)
                .First().Close;
        var warmup = previousSessions.SelectMany(value => value)
            .Select(value => new HistoricalReplayBar(value.OpenTimeUtc, 60,
                value.Open, value.High, value.Low, value.Close)
            { Symbol = index.TradingSymbol, Volume = value.Volume,
                OpenInterest = value.OpenInterest });
        var names = optionInstruments.ToDictionary(value => value.Id,
            value => value.TradingSymbol);
        var marketBars = candles.Where(value => value.InstrumentId == index.Id ||
                (!useRawFuture && future is not null && value.InstrumentId == future.Id))
            .Select(value => new HistoricalReplayBar(value.OpenTimeUtc, 60,
                value.Open, value.High, value.Low, value.Close)
            {
                Symbol = value.InstrumentId == index.Id ? index.TradingSymbol :
                    future!.TradingSymbol,
                Volume = value.Volume,
                OpenInterest = value.OpenInterest
            });
        var rawFutureBars = unmappedPaths.Where(value =>
                value.Source == "GrowwHistoricalApiV1" &&
                value.GrowwSymbol == (useRawFuture ? frontFutureSymbol : future?.GrowwSymbol))
            .Select(value => new HistoricalReplayBar(value.OpenTimeUtc, 60,
                value.Open, value.High, value.Low, value.Close)
            { Symbol = frontFutureSymbol!, Volume = value.Volume,
                OpenInterest = value.OpenInterest, Source = "OfficialHistoricalRepair" });
        var repairedIndexBars = unmappedPaths.Where(value => value.Source == GrowwHistoricalResearchImporter.IndexSource &&
                value.GrowwSymbol == index.GrowwSymbol)
            .Select(value => new HistoricalReplayBar(value.OpenTimeUtc, 60, value.Open, value.High, value.Low, value.Close)
            { Symbol = index.TradingSymbol, Volume = value.Volume, OpenInterest = value.OpenInterest, Source = "OfficialHistoricalRepair" });
        var optionBars = candles.Where(value => names.ContainsKey(value.InstrumentId))
            .Select(value => new HistoricalReplayBar(value.OpenTimeUtc, 60,
                value.Open, value.High, value.Low, value.Close)
            { Symbol = names[value.InstrumentId], Volume = value.Volume,
                OpenInterest = value.OpenInterest });
        var rawOptionBars = unmappedPaths.Where(value =>
                value.Source == "GrowwHistoricalApiV1" &&
                !value.GrowwSymbol.EndsWith("-FUT", StringComparison.Ordinal))
            .Select(value => new HistoricalReplayBar(value.OpenTimeUtc, 60,
                value.Open, value.High, value.Low, value.Close)
            { Symbol = mappedProviderSymbols.GetValueOrDefault(value.GrowwSymbol) ??
                    value.GrowwSymbol, Volume = value.Volume,
                OpenInterest = value.OpenInterest, Source = "OfficialHistoricalRepair" });
        var minuteBars = HistoricalArchivePathCanonicalizer.PreferRecordedBars(
            warmup.Concat(marketBars).Concat(optionBars), rawFutureBars.Concat(repairedIndexBars).Concat(rawOptionBars));
        var bars = minuteBars.Concat(HistoricalBarAggregation.ToFiveMinutes(
                minuteBars.Where(value => value.Symbol == index.TradingSymbol || value.Symbol == frontFutureSymbol)))
            .OrderBy(value => value.AvailableAtUtc)
            .ThenBy(value => value.Symbol, StringComparer.Ordinal).ToArray();
        var chainQuotes = chains.Where(value => value.ReceivedAtUtc >= start &&
                value.Source == "GrowwOptionChainApiV1")
            .SelectMany(value => HistoricalOptionChainParser.ParseQuoteObservations(
                value.PayloadJson, value.ReceivedAtUtc))
            .Select(value => value with
            {
                Symbol = mappedProviderSymbols.GetValueOrDefault(value.Symbol) ??
                    value.Symbol
            });
        var replayQuoteSymbols = names.Values.Append(index.TradingSymbol).ToDictionary(value => value, value => value, StringComparer.Ordinal);
        foreach (var pair in mappedProviderSymbols) replayQuoteSymbols[pair.Key] = pair.Value;
        if (gapAware && feedManifest is not null)
            foreach (var instrument in feedManifest.Instruments.Where(value => value.Market == underlying))
                replayQuoteSymbols.TryAdd(instrument.TradingSymbol, instrument.TradingSymbol);
        var quoteDay = quoteArchive is null ? new HistoricalReplayQuoteArchiveDay([], true) :
            await quoteArchive.ReadReplayAsync(tradingDate, underlying, exchange, replayQuoteSymbols, start, end, cancellationToken,
                streamingOnly: gapAware || feedManifest is not null, tolerateUnreadableShards: gapAware);
        var observedQuotes = quoteDay.Quotes;
        var proxyQuotes = HistoricalArchivePathCanonicalizer.Quotes(
            candles.Where(value => names.ContainsKey(value.InstrumentId))
            .Select(value => new HistoricalReplayQuote(names[value.InstrumentId],
                value.OpenTimeUtc.AddMinutes(1), value.Close)
            { Volume = value.Volume, OpenInterest = value.OpenInterest })
            .Concat(unmappedPaths.Where(value => value.Source == "GrowwHistoricalApiV1" && !value.GrowwSymbol.EndsWith("-FUT",
                StringComparison.Ordinal)).Select(value => new HistoricalReplayQuote(
                mappedProviderSymbols.GetValueOrDefault(value.GrowwSymbol) ??
                    value.GrowwSymbol,
                value.OpenTimeUtc.AddMinutes(1), value.Close)
            { Volume = value.Volume, OpenInterest = value.OpenInterest }))
            .Concat(chainQuotes));
        IReadOnlyList<HistoricalReplayQuote> quotes = HistoricalQuotePaths.Merge([proxyQuotes, observedQuotes]);
        var contracts = datedItems is null
            ? optionInstruments.Where(value => value.ExpiryDate.HasValue &&
                    value.StrikePrice.HasValue && value.LotSize > 0 && value.TickSize > 0)
                .Select(value => new NiftyOptionContractCandidate(value.Id,
                    value.TradingSymbol, value.Type, value.ExpiryDate!.Value,
                    value.StrikePrice!.Value, value.LotSize, value.TickSize)).ToArray()
            : HistoricalDatedOptionContracts.Resolve(datedItems,
                optionInstruments.Select(value => new HistoricalMappedOptionContract(
                    value.Id, exchange, value.TradingSymbol,
                    value.GrowwSymbol ?? "", value.Type, value.ExpiryDate,
                    value.StrikePrice, value.LotSize, value.TickSize)).ToArray(),
                exchange, underlying, tradingDate).ToArray();
        var streamingUniverse = feedManifest is not null && feedWindowComplete && !quoteDay.HasCaptureGaps &&
            datedMaster is not null && HistoricalFeedCaptureEvidence.CoversNormalUniverse(feedManifest, underlying, contracts, tradingDate, bars,
                feedManifest.PolicyReceipt?.Settings.NiftySelection);
        if (gapAware || streamingUniverse)
        {
            quotes = observedQuotes;
            // Once an observed option path exists, a minute's option low/high
            // cannot be applied to a stop revised part-way through that minute.
            // Normal policies consume underlying/future bars, not option OHLC.
            bars = bars.Where(value => value.Symbol == index.TradingSymbol || value.Symbol == frontFutureSymbol).ToArray();
        }
        var optionSymbols = contracts.Select(value => value.TradingSymbol).ToHashSet(StringComparer.Ordinal);
        var optionObservations = quotes.Where(value => optionSymbols.Contains(value.Symbol));
        var manifest = "datedMaster=" + datedMaster?.Fingerprint +
            "|datedMasterCapturedAt=" + datedMaster?.CapturedAtUtc.ToString("O") +
            "|observedQuoteCaptureGaps=" + quoteDay.HasCaptureGaps +
            "|observedQuoteCaptureMode=BufferedBestEffort" +
            "|controlStateVerified=" + controls.InitialStateVerified +
            "|controlStateGaps=" + string.Join(';', controls.Gaps) +
            "|streamingUniverse=" + feedManifest?.UniverseFingerprint +
            "|feedWindowComplete=" + feedWindowComplete +
            "|streamingUniverseScope=" + feedManifest?.UniverseScope +
            "|contractSelection=" + feedManifest?.ContractSelectionVersion +
            "|sessionPolicy=" + feedManifest?.PolicyReceipt?.Fingerprint +
            "|sensexPriorThesisCaptured=" + feedManifest?.SensexPriorThesisStateCaptured +
            "|priorSensexThesis=" + JsonSerializer.Serialize(feedManifest?.PriorSensexThesis) +
            "|barTiming=CompletionTimeModel;recorded-bars-preferred;missing-only-research-repairs" +
            "|previousClose=" + previousClose?.ToString(
                System.Globalization.CultureInfo.InvariantCulture) + "|future=" +
            frontFutureSymbol + "|contracts=" + string.Join(';', contracts
                .OrderBy(value => value.TradingSymbol, StringComparer.Ordinal)
                .Select(value => FormattableString.Invariant(
                    $"{value.TradingSymbol}:{value.Type}:{value.ExpiryDate}:{value.StrikePrice}:{value.LotSize}:{value.TickSize}"))) +
            "|universePages=" + string.Join(';', pageReceipts.Select(value =>
                FormattableString.Invariant(
                    $"{value.ExpiryDate}:{value.Page}:{value.PageSize}:{value.TotalSelectedContracts}:{value.UniverseFingerprint}:{value.FetchedSymbolsJson}")));
        var indexCoverage = HistoricalSessionCoverageEvaluator.Evaluate(tradingDate,
            minuteBars.Where(value => value.Symbol == index.TradingSymbol &&
                value.OpenTimeUtc >= start).Select(value => value.OpenTimeUtc));
        var futureCoverage = frontFutureSymbol is null ? null :
            HistoricalSessionCoverageEvaluator.Evaluate(tradingDate,
                minuteBars.Where(value => value.Symbol == frontFutureSymbol &&
                    value.OpenTimeUtc >= start).Select(value => value.OpenTimeUtc));
        var session = new HistoricalReplaySessionData(tradingDate,
            index.TradingSymbol, bars, quotes, start, end)
        {
            ExchangeCode = exchange,
            ControlEvents = controls.Events,
            ExecutionOptionSymbols = optionSymbols.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
            EligibleOptionUniverseComplete = streamingUniverse,
            ExecutableQuoteSidesComplete = streamingUniverse && optionObservations.Any() &&
                // Feed snapshots include empty/withdrawn books explicitly. Those
                // are unavailable liquidity, not missing recording; fills still
                // require actual sides and cannot fall back to their LTP.
                optionObservations.All(value => value.Source == "GrowwFeedSample" && value.DepthSourceAtUtc.HasValue),
            StrategyInputsComplete = streamingUniverse && (underlying == "BANKNIFTY" ||
                optionObservations.All(value => value.Volume.HasValue && value.OpenInterest.HasValue)),
            PolicyInputManifest = manifest,
            UnsupportedContractCoverage = (feedWindowComplete ? Array.Empty<string>() :
                new[] { "Capture window incomplete: " + (feedManifest is null ? "no recorder manifest" :
                    feedManifest.InterruptionReason ?? $"started {feedManifest.StartedAtUtc:O}; subscribed {feedManifest.SubscribedAtUtc:O}; completed {feedManifest.CompletedAtUtc:O}; gaps={feedManifest.HasGaps}") })
                .Concat(feedManifest?.ContractSelectionVersion != RegularOptionContractSelector.Version
                ? ["Unsupported replay: shared contract-selection receipt missing or version unverified."]
                : feedManifest.ContractPreflight is not { Ready: true }
                    ? ["Unsupported replay: pre-session contract coverage was not verified."]
                    : feedManifest.ContractCoverageIssues).ToArray(),
            OptionChains = chains.Select(value => new HistoricalOptionChainFrame(
                    value.ReceivedAtUtc, value.SourceTimestampUtc,
                    value.ExpiryDate,
                    HistoricalOptionChainParser.Parse(value.PayloadJson))
                { UnderlyingSymbol = index.TradingSymbol })
                .Where(value => value.Strikes.Count > 0 &&
                    value.SourceTimestampUtc <= value.AvailableAtUtc)
                .ToArray(),
            RequiredInputsComplete = indexCoverage.IsComplete &&
                (underlying == "BANKNIFTY" || futureCoverage is { IsComplete: true }) && previousClose.HasValue &&
                datedMaster is not null && controls.InitialStateVerified
                && (underlying != "SENSEX" || feedManifest?.SensexPriorThesisStateCaptured == true)
        };
        return new(session,
            previousClose, frontFutureSymbol, contracts,
            unmappedPaths.Where(value => !value.GrowwSymbol.EndsWith("-FUT",
                    StringComparison.Ordinal) &&
                    !mappedProviderSymbols.ContainsKey(value.GrowwSymbol))
                .GroupBy(value => value.GrowwSymbol, StringComparer.Ordinal)
                .Where(group => group.Select(value => value.OpenTimeUtc)
                    .Distinct().Take(2).Count() == 2)
                .Select(group => group.Key)
                .OrderBy(value => value, StringComparer.Ordinal).ToArray())
        {
            UnderlyingInstrumentId = index.Id,
            SessionPolicyFingerprint = feedManifest?.PolicyReceipt?.Fingerprint,
            CapturedSessionPolicy = gapAware ? feedManifest?.PolicyReceipt : null,
            CaptureSampleSeconds = feedManifest?.SampleSeconds ?? 2,
            UnreadableQuoteIntervals = gapAware ? quoteDay.UnreadableIntervals : [],
            ArchiveReportedCaptureGaps = gapAware && (quoteDay.HasCaptureGaps || feedManifest?.HasGaps == true),
            GapAwarePolicyInputsReady = gapAware && datedMaster is not null && controls.InitialStateVerified &&
                previousClose is > 0m && (underlying == "BANKNIFTY" || frontFutureSymbol is not null) &&
                feedManifest is { ContractPreflight.Ready: true } &&
                feedManifest.PolicyReceipt?.ValidFor(tradingDate) == true &&
                (underlying != "SENSEX" || feedManifest.SensexPriorThesisStateCaptured),
            RecordedContractUniverse = feedManifest is null ? null : new(
                feedManifest.ContractSelectionVersion ?? "legacy-unverified", feedManifest.Instruments),
            RecordedContractCoverageIssues = session.UnsupportedContractCoverage,
            PriorSensexThesis = feedManifest?.PriorSensexThesis,
            ControlStateGaps = controls.Gaps,
            UniversePages = pageReceipts.Select(value =>
                new HistoricalUniversePageEvidence(value.ExpiryDate, value.Page,
                    value.PageSize, value.TotalSelectedContracts,
                    value.UniverseFingerprint,
                    ParseSymbols(value.FetchedSymbolsJson))).ToArray(),
            ProviderSymbolToReplaySymbol = mappedProviderSymbols,
            InstrumentMasterSnapshotFingerprint = datedMaster?.Fingerprint,
            InstrumentMasterCapturedAtUtc = datedMaster?.CapturedAtUtc
        };
    }

    private static string[] ParseSymbols(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<string[]>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
