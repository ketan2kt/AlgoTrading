using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TradingSystem.Application.Execution;
using TradingSystem.Domain.Trading;
using TradingSystem.Infrastructure.Persistence;

namespace TradingSystem.Infrastructure.Execution;

internal sealed class EfHistoricalReplayReportReader(TradingDbContext db)
    : IHistoricalReplayReportReader
{
    private static readonly TimeZoneInfo India = FindIndiaTimeZone();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<HistoricalReplayResearchReport> ReadAsync(DateOnly from, DateOnly endDate,
        string market, HistoricalReplayParameters parameters, CancellationToken cancellationToken)
    {
        parameters.Validate();
        var (exchange, underlying) = market switch
        {
            "nifty" => ("NSE", "NIFTY"),
            "sensex" => ("BSE", "SENSEX"),
            "bank-nifty" => ("NSE", "BANKNIFTY"),
            _ => throw new ArgumentOutOfRangeException(nameof(market))
        };
        var start = ToUtc(from, TimeOnly.MinValue);
        var end = ToUtc(endDate.AddDays(1), TimeOnly.MinValue);
        var indexIds = await db.Instruments.AsNoTracking()
            .Where(value => value.Exchange == exchange && value.TradingSymbol == underlying &&
                            value.Segment == InstrumentSegment.Cash &&
                            value.Type == InstrumentType.Index)
            .Select(value => value.Id).ToArrayAsync(cancellationToken);
        var candles = await db.Candles.AsNoTracking()
            .Where(value => indexIds.Contains(value.InstrumentId) &&
                            value.Source == "Groww" &&
                            value.IntervalSeconds == 60 && value.OpenTimeUtc >= start &&
                            value.OpenTimeUtc < end)
            .Select(value => new { value.InstrumentId, value.OpenTimeUtc, value.Open,
                value.High, value.Low, value.Close, value.Volume, value.OpenInterest })
            .ToListAsync(cancellationToken);
        var chains = await db.OptionChainSnapshots.AsNoTracking()
            .Where(value => indexIds.Contains(value.UnderlyingInstrumentId) &&
                            value.Source == "GrowwOptionChainApiV1" &&
                            value.IsValid &&
                            value.ReceivedAtUtc >= start && value.ReceivedAtUtc < end)
            .OrderBy(value => value.ReceivedAtUtc)
            .Select(value => new { value.ReceivedAtUtc, value.PayloadJson })
            .ToListAsync(cancellationToken);
        var evaluations = await db.StrategyEvaluations.AsNoTracking()
            .Where(value => indexIds.Contains(value.InstrumentId) &&
                            value.RecordedAtUtc >= start && value.RecordedAtUtc < end &&
                            value.OptionSymbol != null && !value.StrategyCode.StartsWith("Hero Zero|") &&
                            !value.StrategyCode.StartsWith("Expansion Pair|"))
            .Select(value => new { value.OptionSymbol, value.StrategyCode,
                value.RecordedAtUtc, value.RegimeConfidence, value.CandleTimeUtc })
            .ToListAsync(cancellationToken);
        var marketPositions = await db.MarketPaperPositions.AsNoTracking()
            .Where(value => value.Market == market && value.ClosedAtUtc >= start &&
                            value.ClosedAtUtc < end && !value.Strategy.StartsWith("Hero Zero|") &&
                            !value.Strategy.StartsWith("Expansion Pair|"))
            .Select(value => new { value.ClosedAtUtc, value.RealisedPnl })
            .ToListAsync(cancellationToken);
        var openedPositions = await db.MarketPaperPositions.AsNoTracking()
            .Where(value => value.Market == market && value.OpenedAtUtc >= start &&
                            value.OpenedAtUtc < end && !value.Strategy.StartsWith("Hero Zero|") &&
                            !value.Strategy.StartsWith("Expansion Pair|"))
            .Select(value => new { value.ExecutionInstrumentId, value.OpenedAtUtc,
                value.Strategy, value.Quantity })
            .ToListAsync(cancellationToken);
        var openedInstrumentIds = openedPositions.Select(value => value.ExecutionInstrumentId)
            .Distinct().ToArray();
        var openedInstruments = await db.Instruments.AsNoTracking()
            .Where(value => openedInstrumentIds.Contains(value.Id))
            .Select(value => new { value.Id, value.TradingSymbol })
            .ToDictionaryAsync(value => value.Id, value => value.TradingSymbol,
                cancellationToken);
        var niftyTrades = market == "nifty"
            ? await db.PaperTradeResults.AsNoTracking()
                .Where(value => value.ClosedAtUtc >= start && value.ClosedAtUtc < end)
                .Select(value => new { value.ClosedAtUtc, value.RealisedPnl })
                .ToListAsync(cancellationToken)
            : [];
        var optionSymbols = evaluations.Select(value => value.OptionSymbol!)
            .Concat(openedPositions.Where(value => openedInstruments.ContainsKey(
                    value.ExecutionInstrumentId))
                .Select(value => openedInstruments[value.ExecutionInstrumentId]))
            .Distinct(StringComparer.Ordinal).ToArray();
        var availableLotSizes = await db.Instruments.AsNoTracking()
            .Where(value => optionSymbols.Contains(value.TradingSymbol) &&
                            value.Exchange == exchange &&
                            value.Segment == InstrumentSegment.FuturesAndOptions &&
                            (value.Type == InstrumentType.CallOption ||
                             value.Type == InstrumentType.PutOption))
            .Select(value => new { value.Id, value.TradingSymbol, value.LotSize,
                value.ExpiryDate, value.StrikePrice, value.TickSize, value.GrowwSymbol })
            .ToListAsync(cancellationToken);
        // Do not pick an arbitrary instrument/lot when the historical master
        // contains more than one row for a trading symbol.
        var ambiguousSymbols = availableLotSizes.GroupBy(value =>
                value.TradingSymbol, StringComparer.Ordinal)
            .Where(group => group.Count() > 1).Select(group => group.Key)
            .ToHashSet(StringComparer.Ordinal);
        var lotSizes = availableLotSizes.GroupBy(value => value.TradingSymbol,
                StringComparer.Ordinal)
            .Where(group => group.Count() == 1).Select(group => group.Single())
            .ToArray();
        var lots = lotSizes.ToDictionary(value => value.TradingSymbol,
            value => value.LotSize, StringComparer.Ordinal);
        var optionIds = lotSizes.Select(value => value.Id).ToArray();
        var optionNames = lotSizes.ToDictionary(value => value.Id, value => value.TradingSymbol);
        var optionCandles = await db.Candles.AsNoTracking()
            .Where(value => optionIds.Contains(value.InstrumentId) &&
                            value.Source == "Groww" &&
                            value.IntervalSeconds == 60 && value.OpenTimeUtc >= start &&
                            value.OpenTimeUtc < end)
            .Select(value => new { value.InstrumentId, value.OpenTimeUtc, value.Open,
                value.High, value.Low, value.Close, value.Volume, value.OpenInterest })
            .ToListAsync(cancellationToken);
        var candleQuotes = optionCandles.Select(value => new HistoricalReplayQuote(
            optionNames[value.InstrumentId], value.OpenTimeUtc.AddMinutes(1), value.Close))
            .ToArray();
        var candlePaths = candleQuotes.Select(value => (value.Symbol,
                Day: LocalDate(value.AvailableAtUtc)))
            .GroupBy(value => value)
            .Where(value => value.Count() >= 30)
            .Select(value => value.Key).ToHashSet();
        var chainQuotes = new List<HistoricalReplayQuote>();
        foreach (var chain in chains)
        {
            try
            {
                var payload = JsonSerializer.Deserialize<ChainPayload>(chain.PayloadJson, JsonOptions);
                if (payload is null) continue;
                foreach (var strike in payload.SelectedStrikes ?? [])
                {
                    Add(strike.Call);
                    Add(strike.Put);
                }
            }
            catch (JsonException) { /* Counted as a missing usable snapshot below. */ }
            void Add(ChainContract? contract)
            {
                if (contract is { LastPrice: > 0m } &&
                    !string.IsNullOrWhiteSpace(contract.TradingSymbol))
                    chainQuotes.Add(new(contract.TradingSymbol, chain.ReceivedAtUtc, contract.LastPrice));
            }
        }
        // A completed historical option bar becomes available at its close. Do not
        // mix a bar-close path with sampled live chain LTPs for the same contract/day.
        var quotes = candleQuotes.Where(value => candlePaths.Contains((value.Symbol,
                LocalDate(value.AvailableAtUtc))))
            .Concat(chainQuotes.Where(value =>
            !candlePaths.Contains((value.Symbol, LocalDate(value.AvailableAtUtc)))))
            .ToArray();
        // These are recorded, option-selected candidate evaluations, including candidates
        // that were not traded. A newly invented entry rule cannot be inferred from them.
        var evaluatedCandidates = evaluations.Where(value => value.OptionSymbol is not null &&
                lots.ContainsKey(value.OptionSymbol))
            .GroupBy(value => (value.CandleTimeUtc, value.OptionSymbol))
            .Select(group => group.OrderBy(value => value.RecordedAtUtc).First())
            .Select(value => new HistoricalReplayCandidate(value.OptionSymbol!,
                value.RecordedAtUtc, value.StrategyCode, value.RegimeConfidence,
                lots[value.OptionSymbol!]))
            .ToArray();
        var positionCandidates = openedPositions.Where(value =>
                !value.Strategy.StartsWith("Expansion Pair|", StringComparison.Ordinal) &&
                openedInstruments.TryGetValue(value.ExecutionInstrumentId, out var symbol) &&
                lots.ContainsKey(symbol))
            .Select(value => new HistoricalReplayCandidate(
                openedInstruments[value.ExecutionInstrumentId], value.OpenedAtUtc,
                "ActualEntry:" + value.Strategy, 1m, value.Quantity));
        var candidates = evaluatedCandidates.Concat(positionCandidates)
            .GroupBy(value => (value.DecidedAtUtc.ToUnixTimeSeconds() / 60, value.Symbol))
            .Select(group => group.OrderBy(value => value.DecidedAtUtc).First())
            .ToArray();
        var days = Enumerable.Range(0, endDate.DayNumber - from.DayNumber + 1)
            .Select(offset => from.AddDays(offset)).ToArray();
        var dailySimulations = days.Select(date => HistoricalSessionReplay.Run(
            quotes.Where(value => LocalDate(value.AvailableAtUtc) == date).ToArray(),
            candidates.Where(value => LocalDate(value.DecidedAtUtc) == date).ToArray(),
            parameters, ToUtc(date, new TimeOnly(15, 30)), exchange)).ToArray();
        var dailyReference = days.Select(date => HistoricalSessionReplay.Run(
            quotes.Where(value => LocalDate(value.AvailableAtUtc) == date).ToArray(),
            candidates.Where(value => LocalDate(value.DecidedAtUtc) == date).ToArray(),
            HistoricalReplayParameters.Default, ToUtc(date, new TimeOnly(15, 30)),
            exchange)).ToArray();
        var simulatedFills = dailySimulations.SelectMany(value => value.Trades)
            .OrderBy(value => value.ExitAtUtc).ToArray();
        decimal running = 0m, highWater = 0m, maximumDrawdown = 0m;
        foreach (var fill in simulatedFills)
        {
            running += fill.NetPnl;
            highWater = Math.Max(highWater, running);
            maximumDrawdown = Math.Max(maximumDrawdown, highWater - running);
        }
        var simulation = new HistoricalReplayResult(simulatedFills,
            dailySimulations.Sum(value => value.CandidateCount),
            dailySimulations.Sum(value => value.MissingEntryQuoteCount),
            dailySimulations.Sum(value => value.MissingExitQuoteCount),
            dailySimulations.Sum(value => value.StaleQuoteCount), running, maximumDrawdown,
            dailySimulations[0].ExecutionAssumption);
        var referenceFills = dailyReference.SelectMany(value => value.Trades)
            .OrderBy(value => value.ExitAtUtc).ToArray();
        decimal referenceEquity = 0m, referencePeak = 0m, referenceDrawdown = 0m;
        foreach (var fill in referenceFills)
        {
            referenceEquity += fill.NetPnl;
            referencePeak = Math.Max(referencePeak, referenceEquity);
            referenceDrawdown = Math.Max(referenceDrawdown, referencePeak - referenceEquity);
        }
        var referenceSimulation = new HistoricalReplayResult(referenceFills,
            dailyReference.Sum(value => value.CandidateCount),
            dailyReference.Sum(value => value.MissingEntryQuoteCount),
            dailyReference.Sum(value => value.MissingExitQuoteCount),
            dailyReference.Sum(value => value.StaleQuoteCount), referenceEquity,
            referenceDrawdown, dailyReference[0].ExecutionAssumption);
        var dayComparisons = days.Select((date, index) => new HistoricalReplayDayComparison(
            date, dailyReference[index].NetPnl, dailySimulations[index].NetPnl,
            dailySimulations[index].NetPnl - dailyReference[index].NetPnl,
            dailyReference[index].Trades.Count, dailySimulations[index].Trades.Count)).ToArray();
        var sessions = days.Select(date =>
        {
            var candleCount = candles.Count(value => LocalDate(value.OpenTimeUtc) == date);
            var indexCoverage = HistoricalSessionCoverageEvaluator.Evaluate(date,
                candles.Where(value => LocalDate(value.OpenTimeUtc) == date)
                    .Select(value => value.OpenTimeUtc));
            var chainCount = chains.Count(value => LocalDate(value.ReceivedAtUtc) == date);
            var optionCandleCount = optionCandles.Count(value =>
                LocalDate(value.OpenTimeUtc.AddMinutes(1)) == date);
            var candidateCount = candidates.Count(value => LocalDate(value.DecidedAtUtc) == date);
            var candidateSymbols = candidates.Where(value => LocalDate(value.DecidedAtUtc) == date)
                .Select(value => value.Symbol).Distinct(StringComparer.Ordinal).ToArray();
            var completeOptionBars = candidateSymbols.Length > 0 &&
                candidateSymbols.All(symbol => HistoricalSessionCoverageEvaluator
                    .Evaluate(date, optionCandles.Where(value =>
                        optionNames[value.InstrumentId] == symbol &&
                        LocalDate(value.OpenTimeUtc) == date)
                    .Select(value => value.OpenTimeUtc)).IsComplete);
            var actual = marketPositions.Where(value => value.ClosedAtUtc.HasValue &&
                    LocalDate(value.ClosedAtUtc.Value) == date)
                .Select(value => value.RealisedPnl)
                .Concat(niftyTrades.Where(value => LocalDate(value.ClosedAtUtc) == date)
                    .Select(value => value.RealisedPnl)).ToArray();
            var status = !indexCoverage.IsComplete || (chainCount < 300 && !completeOptionBars)
                ? "PartialArchive" : candidateCount == 0 ? "NoRecordedCandidates" :
                    completeOptionBars ? "HistoricalOptionBars" : "SampledLtpOnly";
            return new HistoricalReplaySessionCoverage(date, candleCount, chainCount,
                optionCandleCount, candidateCount, actual.Length, actual.Sum(), status,
                indexCoverage.ExpectedMinutes, indexCoverage.MissingMinutes,
                indexCoverage.LongestMissingRunMinutes);
        }).ToArray();
        var trainingDays = days.Length < 2 ? days.Length :
            Math.Clamp((int)Math.Floor(days.Length * 0.70m), 1, days.Length - 1);
        var validationDate = trainingDays < days.Length ? days[trainingDays] : DateOnly.MaxValue;
        var trainingFills = simulatedFills.Where(value => LocalDate(value.EntryAtUtc) <
            validationDate).ToArray();
        var validationFills = simulatedFills.Where(value => LocalDate(value.EntryAtUtc) >=
            validationDate).ToArray();
        var validation = new HistoricalReplayValidation(trainingDays, days.Length - trainingDays,
            trainingFills.Length, validationFills.Length, trainingFills.Sum(value => value.NetPnl),
            validationFills.Sum(value => value.NetPnl),
            days.Length >= 10 && validationFills.Length >= 20 &&
            sessions.All(value => value.Status is "SampledLtpOnly" or "HistoricalOptionBars") &&
            simulation.MissingEntryQuoteCount == 0 && simulation.MissingExitQuoteCount == 0
                ? "ResearchOnly" : "InsufficientCoverage");
        // Fingerprint captures the ordered observed data and parameters, not a mutable run time.
        var fingerprint = Fingerprint(candles.OrderBy(value => value.OpenTimeUtc)
                .ThenBy(value => value.InstrumentId)
                .Select(value => FormattableString.Invariant($"C|{value.InstrumentId}|{value.OpenTimeUtc:O}|{value.Open}|{value.High}|{value.Low}|{value.Close}|{value.Volume}|{value.OpenInterest}"))
            .Concat(optionCandles.OrderBy(value => value.OpenTimeUtc)
                .ThenBy(value => value.InstrumentId)
                .Select(value => FormattableString.Invariant($"O|{value.InstrumentId}|{value.OpenTimeUtc:O}|{value.Open}|{value.High}|{value.Low}|{value.Close}|{value.Volume}|{value.OpenInterest}")))
            .Concat(chains.OrderBy(value => value.ReceivedAtUtc)
                .Select(value => $"CHAIN|{value.ReceivedAtUtc:O}|{value.PayloadJson}"))
            .Concat(availableLotSizes.OrderBy(value => value.TradingSymbol,
                    StringComparer.Ordinal).ThenBy(value => value.Id)
                .Select(value => FormattableString.Invariant($"I|{value.Id}|{value.TradingSymbol}|{value.GrowwSymbol}|{value.ExpiryDate}|{value.StrikePrice}|{value.LotSize}|{value.TickSize}")))
            .Concat(quotes.OrderBy(value => value.AvailableAtUtc)
                .ThenBy(value => value.Symbol, StringComparer.Ordinal)
                .Select(value => FormattableString.Invariant($"Q|{value.AvailableAtUtc:O}|{value.Symbol}|{value.LastPrice}")))
            .Concat(candidates.OrderBy(value => value.DecidedAtUtc)
                .Select(value => FormattableString.Invariant($"S|{value.DecidedAtUtc:O}|{value.Symbol}|{value.Strategy}|{value.Quantity}")))
            .Concat(marketPositions.OrderBy(value => value.ClosedAtUtc)
                .Select(value => FormattableString.Invariant($"B|{value.ClosedAtUtc:O}|{value.RealisedPnl}")))
            .Concat(niftyTrades.OrderBy(value => value.ClosedAtUtc)
                .Select(value => FormattableString.Invariant($"N|{value.ClosedAtUtc:O}|{value.RealisedPnl}")))
            .Append(JsonSerializer.Serialize(parameters))
            .Append("RECORDED_OPTION_CANDIDATES_V2|" +
                (exchange == "BSE" ? PaperTradingCostModel.BseScheduleVersion :
                    PaperTradingCostModel.ScheduleVersion)));
        var limitations = new List<string>
        {
            "This research variant replays option-selected recorded evaluations and actual position entries. It does not regenerate every possible entry from strategy code; some markets have no stored untraded option-selected candidates.",
            "Expansion Pair and Hero Zero baskets are excluded from single-leg simulation; they require separate basket-level replays. Historical actual P&L still includes them.",
            "The historical option-chain archive covers only sampled near-money strikes and last traded prices. Missing contracts, gaps and older sessions cannot be backfilled from this dataset.",
            "Recorded evaluation candidates use one contract lot from instrument metadata; actual position candidates use their recorded quantity. Simulated quantities are therefore not a uniform capital policy.",
            "Modeled slippage and paper charges are not actual executable bid/ask fills. No profitability comparison is valid until archive and candidate coverage are sufficient.",
            "The modeled NSE/BSE paper charge schedules are estimates; confirm date-effective rates and contract notes before treating net results as realised economics.",
            "Drawdown is computed from closed simulated trades, not intratrade mark-to-market exposure.",
            "The baseline is historical realised paper P&L; comparing it directly to this incomplete candidate simulation is not a controlled A/B backtest.",
            "The replay has no route to broker execution or live trading state."
        };
        if (ambiguousSymbols.Count > 0)
            limitations.Add($"{ambiguousSymbols.Count} contract symbols had duplicate instrument metadata and were excluded rather than assigned an arbitrary lot size.");
        return new(from, endDate, market, "RECORDED_OPTION_CANDIDATES_V2", fingerprint,
            parameters, sessions, simulation, referenceSimulation, dayComparisons,
            sessions.Sum(value => value.BaselineNetPnl),
            validation, limitations);
    }

    private static string Fingerprint(IEnumerable<string> lines)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var line in lines)
            hash.AppendData(Encoding.UTF8.GetBytes(line + "\n"));
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static DateOnly LocalDate(DateTimeOffset time) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(time, India).DateTime);

    private static DateTimeOffset ToUtc(DateOnly date, TimeOnly time)
    {
        var local = date.ToDateTime(time);
        return new DateTimeOffset(local, India.GetUtcOffset(local)).ToUniversalTime();
    }

    private static TimeZoneInfo FindIndiaTimeZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("India Standard Time"); }
    }

    private sealed record ChainPayload(IReadOnlyList<ChainStrike>? SelectedStrikes);
    private sealed record ChainStrike(ChainContract? Call, ChainContract? Put);
    private sealed record ChainContract(string TradingSymbol, decimal LastPrice);
}
