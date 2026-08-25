namespace TradingSystem.Application.Execution;

public sealed record ReplayPricePoint(DateTimeOffset ObservedAtUtc, decimal Price);

public sealed record ReplayTradeInput(Guid SignalId, DateTimeOffset EntryTimeUtc,
    decimal EntryPrice, decimal ActualExitPrice, int Quantity, decimal ActualNetPnl,
    string Regime, bool? ShadowWouldPermit, IReadOnlyList<ReplayPricePoint> PricePath);

public sealed record ReplayMetrics(int Trades, int Wins, decimal NetPnl, decimal WinRate,
    decimal Expectancy, decimal ProfitFactor, decimal MaximumDrawdown);

public sealed record ReplayVariantResult(string Code, string Description,
    ReplayMetrics Training, ReplayMetrics Validation, int CoveredTrades,
    ReplayRobustness Robustness);

public sealed record ReplayRobustness(int ValidationDays, int PositiveValidationDays,
    decimal PositiveDayRate, string Verdict, string Reason);

public sealed record PaperStrategyReplayReport(int SourceTrades, int TradesWithPricePath,
    int RejectedEvaluationsWithoutOptionPath, decimal TrainingFraction,
    IReadOnlyList<ReplayVariantResult> Variants, IReadOnlyList<string> Limitations);

public static class PaperStrategyReplay
{
    private const decimal TrainingFraction = 0.70m;

    public static PaperStrategyReplayReport Analyze(IReadOnlyList<ReplayTradeInput> source,
        int rejectedEvaluationsWithoutOptionPath)
    {
        var ordered = source.OrderBy(value => value.EntryTimeUtc).ToArray();
        var split = ordered.Length < 2 ? ordered.Length : Math.Clamp(
            (int)Math.Floor(ordered.Length * TrainingFraction), 1, ordered.Length - 1);
        var splitTime = split <= 0 ? DateTimeOffset.MinValue : ordered[split - 1].EntryTimeUtc;
        var variants = new List<ReplayVariantResult>
        {
            Cohort("ACTUAL_BASELINE", "All executed trades using their realised net P&L.", ordered,
                split, _ => true),
            Cohort("STRUCTURE_PERMITTED", "Executed trades the recorded structure gate would permit.",
                ordered, split, value => value.ShadowWouldPermit == true),
            Cohort("DIRECTIONAL_REGIME", "Executed trades outside compression, range and unknown regimes.",
                ordered, split, value => value.Regime is not ("LowVolatilityCompression" or "RangeBound" or "Unknown"))
        };

        var pathCovered = ordered.Where(value => value.PricePath.Count >= 3).ToArray();
        variants.Add(PathVariant("FIXED_1R_10_PERCENT",
            "10% premium stop and equal 10% target; otherwise exits at the observed terminal price.",
            pathCovered, splitTime, ReplayFixedOneR));
        variants.Add(PathVariant("PROFIT_LOCK_1R",
            "10% premium stop/target with stop moved to +2% after +5% and +6% after +8%.",
            pathCovered, splitTime, ReplayProfitLock));

        return new(ordered.Length, pathCovered.Length, rejectedEvaluationsWithoutOptionPath,
            TrainingFraction, variants,
            [
                "Entry-filter variants are retrospective cohorts of executed trades, not simulated rejected trades.",
                "Rejected setups lack a complete historical option-price path and therefore receive no invented P&L.",
                "Minute samples cannot prove the ordering of stop and target touches between observations.",
                "Results are split chronologically 70/30 and are research evidence, not a profitability claim."
            ]);
    }

    private static ReplayVariantResult Cohort(string code, string description,
        ReplayTradeInput[] rows, int split, Func<ReplayTradeInput, bool> include)
    {
        var training = rows.Take(split).Where(include).Select(value => value.ActualNetPnl).ToArray();
        var validation = rows.Skip(split).Where(include).Select(value => value.ActualNetPnl).ToArray();
        var validationRows = rows.Skip(split).Where(include).ToArray();
        return new(code, description, Metrics(training), Metrics(validation),
            training.Length + validation.Length, Assess(rows.Take(split).Where(include).ToArray(),
                validationRows, value => value.ActualNetPnl));
    }

    private static ReplayVariantResult PathVariant(string code, string description,
        ReplayTradeInput[] covered, DateTimeOffset splitTime, Func<ReplayTradeInput, decimal> replay)
    {
        if (covered.Length == 0)
            return new(code, description, Metrics([]), Metrics([]), 0,
                new(0, 0, 0m, "InsufficientEvidence", "No complete option-price paths are available."));
        var trainingRows = covered.Where(value => value.EntryTimeUtc <= splitTime).ToArray();
        var validationRows = covered.Where(value => value.EntryTimeUtc > splitTime).ToArray();
        var training = trainingRows.Select(replay).ToArray();
        var validation = validationRows.Select(replay).ToArray();
        return new(code, description, Metrics(training), Metrics(validation), covered.Length,
            Assess(trainingRows, validationRows, replay));
    }

    private static ReplayRobustness Assess(IReadOnlyList<ReplayTradeInput> trainingRows,
        IReadOnlyList<ReplayTradeInput> validationRows, Func<ReplayTradeInput, decimal> pnl)
    {
        var validationDays = validationRows.GroupBy(SessionDate)
            .Select(group => group.Sum(pnl)).ToArray();
        var positiveDays = validationDays.Count(value => value > 0m);
        var positiveDayRate = validationDays.Length == 0 ? 0m :
            (decimal)positiveDays / validationDays.Length;
        var training = Metrics(trainingRows.Select(pnl).ToArray());
        var validation = Metrics(validationRows.Select(pnl).ToArray());

        if (validation.Trades < 10 || validationDays.Length < 3)
            return new(validationDays.Length, positiveDays, positiveDayRate,
                "InsufficientEvidence",
                $"Needs at least 10 validation trades across 3 IST sessions; has {validation.Trades} across {validationDays.Length}.");
        if (training.Expectancy <= 0m)
            return new(validationDays.Length, positiveDays, positiveDayRate, "Rejected",
                "Training expectancy is not positive.");
        if (validation.Expectancy <= 0m || validation.ProfitFactor <= 1m)
            return new(validationDays.Length, positiveDays, positiveDayRate, "Rejected",
                "The chronological validation sample has negative expectancy or profit factor at/below 1.00.");
        if (positiveDayRate < 0.50m)
            return new(validationDays.Length, positiveDays, positiveDayRate, "Rejected",
                "Fewer than half of the validation sessions are net positive.");
        return new(validationDays.Length, positiveDays, positiveDayRate, "Candidate",
            "Positive training and validation expectancy, validation profit factor above 1.00, and at least half of validation sessions positive.");
    }

    private static DateOnly SessionDate(ReplayTradeInput trade) =>
        DateOnly.FromDateTime(trade.EntryTimeUtc.ToOffset(TimeSpan.FromMinutes(330)).Date);

    private static decimal ReplayFixedOneR(ReplayTradeInput trade)
    {
        var stop = trade.EntryPrice * 0.90m;
        var target = trade.EntryPrice * 1.10m;
        var exit = trade.PricePath.OrderBy(value => value.ObservedAtUtc)
            .Select(value => value.Price).FirstOrDefault(value => value <= stop || value >= target);
        if (exit <= 0) exit = trade.ActualExitPrice;
        return NetPnl(trade, exit);
    }

    private static decimal ReplayProfitLock(ReplayTradeInput trade)
    {
        var stop = trade.EntryPrice * 0.90m;
        var target = trade.EntryPrice * 1.10m;
        foreach (var point in trade.PricePath.OrderBy(value => value.ObservedAtUtc))
        {
            if (point.Price >= trade.EntryPrice * 1.08m) stop = Math.Max(stop, trade.EntryPrice * 1.06m);
            else if (point.Price >= trade.EntryPrice * 1.05m) stop = Math.Max(stop, trade.EntryPrice * 1.02m);
            if (point.Price <= stop || point.Price >= target) return NetPnl(trade, point.Price);
        }
        return NetPnl(trade, trade.ActualExitPrice);
    }

    private static decimal NetPnl(ReplayTradeInput trade, decimal exitPrice)
    {
        var gross = (exitPrice - trade.EntryPrice) * trade.Quantity;
        var costs = PaperTradingCostModel.CalculateOptionCharges([
            new(PaperOptionTransactionSide.Buy, trade.EntryPrice, trade.Quantity),
            new(PaperOptionTransactionSide.Sell, exitPrice, trade.Quantity)
        ]).Total;
        return decimal.Round(gross - costs, 2, MidpointRounding.AwayFromZero);
    }

    private static ReplayMetrics Metrics(decimal[] values)
    {
        if (values.Length == 0) return new(0, 0, 0m, 0m, 0m, 0m, 0m);
        var equity = 0m; var peak = 0m; var maximumDrawdown = 0m;
        foreach (var value in values)
        {
            equity += value;
            peak = Math.Max(peak, equity);
            maximumDrawdown = Math.Max(maximumDrawdown, peak - equity);
        }
        var wins = values.Count(value => value > 0);
        return new(values.Length, wins, values.Sum(), (decimal)wins / values.Length,
            values.Average(), PaperTradeResearchAnalyzer.ProfitFactor(values), maximumDrawdown);
    }
}
