using TradingSystem.Application.Strategies;
using TradingSystem.Domain.Trading;

namespace TradingSystem.Application.Execution;

public sealed record MiddayChopConfirmationDecision(bool Applies, bool Permitted,
    IReadOnlyList<string> Reasons);

/// <summary>
/// Adds a completed-five-minute confirmation only during the observed midday window
/// and only when the existing structure classifier sees chop/transition conditions.
/// </summary>
public static class MiddayChopConfirmationPolicy
{
    private static readonly TimeOnly WindowStart = new(11, 0);
    private static readonly TimeOnly WindowEnd = new(13, 30);

    public static MiddayChopConfirmationDecision Evaluate(TimeOnly indiaTime,
        IReadOnlyList<StrategyPriceBar> completedBars, Direction direction,
        MarketStructureQualitySnapshot structure, int sourceIntervalSeconds)
    {
        ArgumentNullException.ThrowIfNull(completedBars);
        ArgumentNullException.ThrowIfNull(structure);

        if (indiaTime < WindowStart || indiaTime > WindowEnd)
            return new(false, true, ["Outside the midday chop-confirmation window."]);

        var choppy = structure.State is MarketStructureQualityState.NoisyChop or
                         MarketStructureQualityState.StructuredRange ||
                     structure.State == MarketStructureQualityState.VolatilityTransition &&
                     structure.ChopScore >= .50m;
        if (!choppy)
            return new(false, true, ["Midday structure is not classified as choppy; normal trend entries remain eligible."]);

        var fiveMinuteBars = AggregateFiveMinuteBars(completedBars, sourceIntervalSeconds);
        if (fiveMinuteBars.Length < 3)
            return new(true, false,
                ["Midday chop is active; at least three completed five-minute bars are needed for a confirmation."]);

        var last = fiveMinuteBars[^1];
        var previous = fiveMinuteBars[^2];
        var prior = fiveMinuteBars[^3];
        if (last.OpenTimeUtc - previous.OpenTimeUtc != TimeSpan.FromMinutes(5) ||
            previous.OpenTimeUtc - prior.OpenTimeUtc != TimeSpan.FromMinutes(5))
            return new(true, false,
                ["Midday chop is active; the latest three completed five-minute bars are not continuous, so confirmation is unavailable."]);
        var alignedClose = direction == Direction.Buy
            ? last.Close > last.Open && last.Close > previous.Close
            : last.Close < last.Open && last.Close < previous.Close;
        var structureBreak = direction == Direction.Buy
            ? last.Close > Math.Max(previous.High, prior.High)
            : last.Close < Math.Min(previous.Low, prior.Low);
        var pullbackResume = direction == Direction.Buy
            ? (previous.Close <= previous.Open || previous.Close < prior.Close) &&
              last.Close > previous.High && last.Close > last.Open
            : (previous.Close >= previous.Open || previous.Close > prior.Close) &&
              last.Close < previous.Low && last.Close < last.Open;

        var permitted = alignedClose && (structureBreak || pullbackResume);
        return permitted
            ? new(true, true,
                ["A completed five-minute direction-aligned close confirmed a structure break or pullback resumption."])
            : new(true, false,
                ["Midday chop is active; waiting for a completed five-minute direction-aligned structure break or pullback resumption."]);
    }

    private static StrategyPriceBar[] AggregateFiveMinuteBars(
        IReadOnlyList<StrategyPriceBar> source, int sourceIntervalSeconds)
    {
        if (sourceIntervalSeconds == 300)
            return source.Where(value => value.OpenTimeUtc.ToUnixTimeSeconds() % 300 == 0)
                .ToArray();
        if (sourceIntervalSeconds <= 0 || sourceIntervalSeconds >= 300 ||
            300 % sourceIntervalSeconds != 0)
            return [];

        var expectedPerBar = 300 / sourceIntervalSeconds;
        return source.GroupBy(value => value.OpenTimeUtc.ToUnixTimeSeconds() / 300)
            .Where(group => group.Count() == expectedPerBar)
            .OrderBy(group => group.Key)
            .Select(group =>
            {
                var bars = group.OrderBy(value => value.OpenTimeUtc).ToArray();
                if (bars.Zip(bars.Skip(1), (left, right) =>
                        right.OpenTimeUtc - left.OpenTimeUtc)
                    .Any(gap => gap != TimeSpan.FromSeconds(sourceIntervalSeconds)))
                    return null;
                return new StrategyPriceBar(
                    DateTimeOffset.FromUnixTimeSeconds(group.Key * 300),
                    bars[0].Open, bars.Max(value => value.High), bars.Min(value => value.Low),
                    bars[^1].Close, bars.Sum(value => value.Volume));
            }).Where(value => value is not null).Cast<StrategyPriceBar>().ToArray();
    }
}
