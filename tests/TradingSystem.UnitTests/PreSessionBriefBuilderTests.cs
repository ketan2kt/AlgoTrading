using TradingSystem.Application.MarketData;

namespace TradingSystem.UnitTests;

public sealed class PreSessionBriefBuilderTests
{
    private static readonly DateOnly Session = new(2026, 10, 27);
    private static readonly DateTimeOffset Captured = new(2026, 10, 27, 3, 15, 0, TimeSpan.Zero);

    [Fact]
    public void DoesNotInventOpeningDirectionWithoutAnIndicativePrice()
    {
        var brief = PreSessionBriefBuilder.Build(Source(indicative: null));

        Assert.Equal("Unclear", brief.Bias);
        Assert.Contains(brief.Evidence, value => value.Name == "GIFT Nifty" &&
            value.Status == "Unavailable" && value.SourceAtUtc is null);
        Assert.Contains(brief.Limitations, value => value.Contains("No validated pre-open"));
    }

    [Fact]
    public void GapWatchHasExplicitInvalidationAndDoesNotAuthorizeTrade()
    {
        var brief = PreSessionBriefBuilder.Build(Source(indicative: 111m));

        Assert.Equal("Upside gap watch", brief.Bias);
        Assert.Contains(brief.Scenarios, value => value.Contains("return below it invalidates"));
        Assert.Contains(brief.Limitations, value => value.Contains("never changes signals"));
    }

    [Fact]
    public void PreviousSessionOptionInterestIsNotCalledTodaysBuildup()
    {
        var brief = PreSessionBriefBuilder.Build(Source(indicative: null));

        var oi = Assert.Single(brief.Evidence, value => value.Name == "Option OI concentration");
        Assert.Equal("PreviousSession", oi.Status);
        Assert.Contains("not current-session OI buildup", oi.Note);
    }

    [Fact]
    public void StrongPriorCloseProducesSavedLowConfidenceHypothesis()
    {
        var source = Source(indicative: null) with
        {
            PreviousOpen = 100m,
            PreviousClose = 109m,
            PreviousHigh = 110m,
            PreviousLow = 100m
        };
        var brief = PreSessionBriefBuilder.Build(source);

        Assert.Equal("Up", brief.Judgement);
        Assert.Equal("Low", brief.Confidence);
        Assert.Contains("prior session closed strongly", brief.JudgementReason);
    }

    [Fact]
    public void ConflictingIndicativeGapLeavesJudgementUnclear()
    {
        var source = Source(indicative: 90m) with
        {
            PreviousOpen = 100m,
            PreviousClose = 109m,
            PreviousHigh = 110m,
            PreviousLow = 100m
        };

        Assert.Equal("Unclear", PreSessionBriefBuilder.Build(source).Judgement);
    }

    private static PreSessionSourceData Source(decimal? indicative) => new(
        "nifty", Session, "Overnight", Captured, 101m, 105m, 110m, 100m,
        Captured.AddDays(-1), indicative,
        indicative is null ? null : Captured.AddMinutes(25),
        112m, 98m, Session.AddDays(2), Captured.AddDays(-1));
}
