using System.Reflection;
using System.Text.Json;
using TradingSystem.Application.Execution;
using TradingSystem.Application.Risk;
using TradingSystem.Application.Strategies;

namespace TradingSystem.UnitTests;

public sealed class TradingSessionRegressionGateTests
{
    private static readonly DateTimeOffset EvaluationTime =
        new(2026, 10, 19, 10, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions JsonOptions = new()
        { PropertyNameCaseInsensitive = true };

    [Fact]
    [Trait("Category", "DeploymentRegression")]
    public void NineteenthOctoberFailureSignatureCannotRecur()
    {
        var fixture = LoadFixture();

        foreach (var stop in fixture.Nifty.EarlyStopEvents)
        {
            var decision = PersistentDirectionalStopPolicy.Evaluate(true, true,
                TimeSpan.FromSeconds(stop.SecondsAfterEntry), stop.AdverseRiskMultiple, false);
            Assert.True(decision.Deferred);
            Assert.False(decision.ShouldExit);
        }

        var recovery = AdaptivePaperTradePolicy.Evaluate(fixture.Nifty.EntriesToday,
            fixture.Nifty.MaximumEntries, fixture.Nifty.ConsecutiveLosses,
            fixture.Nifty.MaximumConsecutiveLosses,
            EvaluationTime.AddMinutes(-fixture.Nifty.LastLossMinutesAgo), EvaluationTime,
            fixture.Nifty.LossCooldownMinutes, true, fixture.Nifty.DirectionalReserve);
        Assert.True(recovery.Permitted);

        var exhausted = AdaptivePaperTradePolicy.Evaluate(
            fixture.Nifty.MaximumEntries + fixture.Nifty.DirectionalReserve,
            fixture.Nifty.MaximumEntries, fixture.Nifty.ConsecutiveLosses,
            fixture.Nifty.MaximumConsecutiveLosses,
            EvaluationTime.AddMinutes(-fixture.Nifty.LastLossMinutesAgo), EvaluationTime,
            fixture.Nifty.LossCooldownMinutes, true, fixture.Nifty.DirectionalReserve);
        Assert.False(exhausted.Permitted);

        Assert.False(DirectionFlipCooldownPolicy.Evaluate(
            EvaluationTime.AddMinutes(-fixture.Sensex.RapidOppositeExitMinutesAgo),
            EvaluationTime).Permitted);
        Assert.True(DirectionFlipCooldownPolicy.Evaluate(
            EvaluationTime.AddMinutes(-fixture.Sensex.CompletedResetMinutesAgo),
            EvaluationTime).Permitted);

        var baseline = fixture.HeroZero.BaselineBars.Select((bar, index) =>
            ToPriceBar(bar, EvaluationTime.AddMinutes(index - 10))).ToArray();
        var weak = HeroZeroExpansionPolicy.Evaluate(
            [.. baseline, ToPriceBar(fixture.HeroZero.WeakExpansion, EvaluationTime)]);
        var decisive = HeroZeroExpansionPolicy.Evaluate(
            [.. baseline, ToPriceBar(fixture.HeroZero.DecisiveExpansion, EvaluationTime)]);
        Assert.False(weak.Permitted);
        Assert.True(decisive.Permitted);
    }

    [Fact]
    [Trait("Category", "DeploymentRegression")]
    public void SafetyBoundariesRemainImmediate()
    {
        Assert.True(PersistentDirectionalStopPolicy.Evaluate(true, true,
            TimeSpan.FromMinutes(2), 1.1m, true).ShouldExit);
        Assert.True(PersistentDirectionalStopPolicy.Evaluate(true, true,
            TimeSpan.FromMinutes(2),
            PersistentDirectionalStopPolicy.CatastrophicAdverseRiskMultiple,
            false).ShouldExit);
        Assert.True(PersistentDirectionalStopPolicy.Evaluate(false, true,
            TimeSpan.FromSeconds(20), 1m, false).ShouldExit);
    }

    private static SessionFixture LoadFixture()
    {
        const string suffix = "RegressionFixtures.2026-10-19-failure-signature.json";
        var assembly = Assembly.GetExecutingAssembly();
        var resource = assembly.GetManifestResourceNames().Single(name =>
            name.EndsWith(suffix, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resource) ??
                           throw new InvalidOperationException($"Missing fixture {resource}.");
        return JsonSerializer.Deserialize<SessionFixture>(stream, JsonOptions) ??
               throw new InvalidOperationException("The regression fixture is invalid.");
    }

    private static StrategyPriceBar ToPriceBar(PriceBar bar, DateTimeOffset time) =>
        new(time, bar.Open, bar.High, bar.Low, bar.Close);

    private sealed record SessionFixture(DateOnly SessionDate, string Provenance,
        NiftyFixture Nifty, SensexFixture Sensex, HeroZeroFixture HeroZero);
    private sealed record NiftyFixture(int EntriesToday, int ConsecutiveLosses,
        int MaximumEntries, int DirectionalReserve, int MaximumConsecutiveLosses,
        int LossCooldownMinutes, int LastLossMinutesAgo,
        IReadOnlyList<StopEvent> EarlyStopEvents);
    private sealed record StopEvent(int SecondsAfterEntry, decimal AdverseRiskMultiple);
    private sealed record SensexFixture(int RapidOppositeExitMinutesAgo,
        int CompletedResetMinutesAgo);
    private sealed record HeroZeroFixture(IReadOnlyList<PriceBar> BaselineBars,
        PriceBar WeakExpansion, PriceBar DecisiveExpansion);
    private sealed record PriceBar(decimal Open, decimal High, decimal Low, decimal Close);
}
