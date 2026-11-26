using TradingSystem.Application.Execution;
using TradingSystem.Application.Strategies;
using TradingSystem.Domain.Trading;

namespace TradingSystem.UnitTests;

public sealed class BankNiftyConfirmedTrailTests
{
    private static readonly DateTimeOffset OpenedAt = new(2026, 11, 11, 6, 10, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(Direction.Buy, 59990, 60010)]
    [InlineData(Direction.Sell, 60010, 59990)]
    public void EarlyFloorRequiresAConfirmedGainAndSustainedBreachWithAdverseSpot(
        Direction direction, int adverse, int favourable)
    {
        var first = Evaluate(new(), 1, 103m, direction: direction);
        Assert.Null(first.State.Floor);
        var armed = Evaluate(first.State, 3, 104m, direction: direction);
        Assert.Equal(0m, armed.State.Floor);
        Assert.False(armed.Exit);

        var unconfirmed = Evaluate(armed.State, 5, 99m, direction: direction);
        Assert.True(unconfirmed.NeedsSpot);
        var safe = Evaluate(armed.State, 5, 99m, direction: direction, spot: favourable);
        Assert.False(safe.Exit);
        var breach = Evaluate(safe.State, 7, 99m, direction: direction, spot: adverse);
        Assert.False(breach.Exit);
        var duplicate = Evaluate(breach.State, 11, 99m, direction: direction, spot: adverse, sourceSecond: 7);
        Assert.False(duplicate.Exit);
        Assert.Null(Evaluate(breach.State, 11, 100m, direction: direction, spot: adverse, sourceSecond: 7).State.FirstBreach);
        Assert.False(Evaluate(duplicate.State, 10, 99m, direction: direction, spot: adverse).Exit);
        Assert.True(Evaluate(duplicate.State, 11, 99m, direction: direction, spot: adverse).Exit);
    }

    [Fact]
    public void HigherFloorSurvivesRestartAndDoesNotRatchetDown()
    {
        var first = Evaluate(new(), 1, 108m);
        var armed = Evaluate(first.State, 3, 109m);
        Assert.True(armed.State.Floor > 0m);
        var restarted = new BankNiftyTrailState(Floor: armed.State.Floor);
        var lower = Evaluate(restarted, 15, 104m);
        Assert.Equal(armed.State.Floor, lower.State.Floor);
        Assert.True(lower.Exit);
    }

    [Fact]
    public void FutureStaleAndThinBooksCannotAdvanceOrBridgeConfirmation()
    {
        var state = Evaluate(Evaluate(new(), 1, 103m).State, 3, 104m).State;
        var breach = Evaluate(state, 5, 99m, spot: 59990);
        foreach (var invalid in new[]
        {
            Evaluate(breach.State, 9, 99m, spot: 59990, sourceSecond: 10),
            Evaluate(breach.State, 37, 99m, spot: 59990, sourceSecond: 6),
            Evaluate(breach.State, 9, 99m, spot: 59990, size: 29)
        })
        {
            Assert.False(invalid.Exit);
            Assert.Null(invalid.State.FirstBreach);
            Assert.Equal(0m, invalid.State.Floor);
        }
        Assert.False(Evaluate(breach.State, 16, 99m, spot: 59990).Exit);
    }

    [Fact]
    public void RuleIsLimitedToTheNormalBankNiftyStrategy()
    {
        const string strategy = BankNiftyFailedSetupCooldownPolicy.Strategy;
        Assert.True(BankNiftyConfirmedTrail.Applies("bank-nifty", strategy));
        Assert.False(BankNiftyConfirmedTrail.Applies("sensex", strategy));
        Assert.False(BankNiftyConfirmedTrail.Applies("nifty", strategy));
        Assert.False(BankNiftyConfirmedTrail.Applies("bank-nifty", "Hero Zero|example"));
    }

    [Theory]
    [InlineData(Direction.Buy, 59999)]
    [InlineData(Direction.Sell, 60001)]
    public void SmallPullbackThroughEntryCannotTriggerTheZeroFloor(Direction side, int spot)
    {
        var state = Evaluate(Evaluate(new(), 1, 103m, side).State, 3, 104m, side).State;
        var first = Evaluate(state, 5, 99m, side, spot);
        var next = Evaluate(first.State, 10, 99m, side, spot);
        Assert.False(next.Exit);
        Assert.Null(next.State.FirstBreach);
        Assert.Equal(0m, next.State.Floor);
    }

    [Theory]
    [InlineData(Direction.Buy, 59990)]
    [InlineData(Direction.Sell, 60010)]
    public void EntryBoundaryNeedsAFullyPostEntryCompletedCloseAndFreshEvidence(Direction side, int spot)
    {
        var signal = new StrategyPriceBar(OpenedAt.AddMinutes(-1), 60000m, 60002m, 59998m, 60000m);
        var last = new StrategyPriceBar(OpenedAt.AddMinutes(1), 60000m, 60010m, 59990m, spot);
        bool Failed(StrategyPriceBar bar, DateTimeOffset now) => BankNiftyConfirmedTrail.HasClosedThroughEntryBoundary(
            [signal, bar], OpenedAt.AddSeconds(1), now, side, spot, 60);
        Assert.False(Failed(last, OpenedAt.AddSeconds(119))); // Incomplete candle.
        Assert.True(Failed(last, OpenedAt.AddSeconds(125)));
        Assert.False(Failed(last, OpenedAt.AddMinutes(3))); // Stale candle.
        Assert.False(Failed(last with { OpenTimeUtc = OpenedAt }, OpenedAt.AddSeconds(65))); // Entry minute.
        Assert.False(BankNiftyConfirmedTrail.HasClosedThroughEntryBoundary(null,
            OpenedAt, OpenedAt.AddMinutes(3), side, spot, 60));
        Assert.False(BankNiftyConfirmedTrail.HasClosedThroughEntryBoundary([signal, last],
            OpenedAt, OpenedAt.AddSeconds(125), side, 60000m, 60)); // Price reclaimed boundary.
    }

    [Fact]
    public void November17OpeningRetracementDoesNotBreakTheEntryBoundary()
    {
        var entry = new DateTimeOffset(2026, 11, 17, 9, 45, 6, TimeSpan.FromMinutes(330));
        // Observed Upstox chart bars; a structural check, not simulated option fills.
        StrategyPriceBar[] bars = [
            new(entry.AddSeconds(-66), 61859.05m, 61885.85m, 61852.55m, 61870.50m),
            new(entry.AddSeconds(-6), 61880.35m, 61915.55m, 61870.60m, 61905.85m)];
        Assert.False(BankNiftyConfirmedTrail.HasClosedThroughEntryBoundary(bars, entry,
            new(2026, 11, 17, 9, 46, 48, entry.Offset), Direction.Buy, 61876.55m, 60));
        bars = [.. bars, new(entry.AddSeconds(54), 61905.70m, 61908.95m, 61876.55m, 61884.65m)];
        Assert.False(BankNiftyConfirmedTrail.HasClosedThroughEntryBoundary(bars, entry,
            new(2026, 11, 17, 9, 47, 3, entry.Offset), Direction.Buy, 61884.65m, 60));
    }

    [Theory]
    [InlineData(Direction.Buy)]
    [InlineData(Direction.Sell)]
    public void EarlierPositiveFloorRequiresClosedContinuationAfterTheProfitLock(Direction direction)
    {
        var at = OpenedAt.AddMinutes(3).AddSeconds(2);
        var lockAt = OpenedAt.AddMinutes(2).AddSeconds(2);
        StrategyPriceBar Bar(int minute, decimal open, decimal high, decimal low, decimal close) =>
            direction == Direction.Buy
                ? new(OpenedAt.AddMinutes(minute), open, high, low, close)
                : new(OpenedAt.AddMinutes(minute), 200 - open, 200 - low, 200 - high, 200 - close);
        StrategyPriceBar[] bars = [Bar(1, 100, 110, 99, 109), Bar(2, 109, 110, 103, 104)];
        BankNiftyTrailDecision Step(BankNiftyTrailState state, DateTimeOffset now, decimal bid,
            IReadOnlyList<StrategyPriceBar>? supplied = null) => BankNiftyConfirmedTrail.Evaluate(
                state, OpenedAt, now, direction, 100, 10, 30, 150, 0,
                new(bid, bid + .05m, 150, now, now), completedBars: supplied);
        var first = Step(new(), lockAt.AddSeconds(-2), 105.2m);
        var locked = Step(first.State, lockAt, 105.3m);
        Assert.Equal(lockAt, locked.State.ProfitLockAt);
        Assert.Equal(0m, locked.State.Floor);
        Assert.True(locked.NeedsStructure);
        var pullback = Step(locked.State, at, 105.4m, bars);
        Assert.Equal(0m, pullback.State.Floor);
        Assert.Null(pullback.State.ContinuationAt);
        Assert.False(pullback.NeedsStructure);

        bars = [.. bars, Bar(3, 104, 112, 103, 111)];
        var beforeClose = Step(pullback.State, OpenedAt.AddMinutes(4).AddSeconds(-1), 106, bars);
        Assert.Null(beforeClose.State.ContinuationAt);
        var resumed = Step(beforeClose.State, OpenedAt.AddMinutes(4).AddSeconds(1), 106.1m, bars);
        Assert.NotNull(resumed.State.ContinuationAt);
        Assert.True(resumed.State.Floor > 0);
        Assert.False(resumed.Exit);
        Assert.True(Step(resumed.State, OpenedAt.AddMinutes(4).AddSeconds(3), 103).Exit);
    }

    [Fact]
    public void ContinuationCannotUseAnEntryMinuteAFutureBarOrAGap()
    {
        var lockAt = OpenedAt.AddMinutes(2).AddSeconds(2);
        StrategyPriceBar[] valid = [
            new(OpenedAt.AddMinutes(1), 100, 110, 99, 109),
            new(OpenedAt.AddMinutes(2), 109, 110, 103, 104),
            new(OpenedAt.AddMinutes(3), 104, 112, 103, 111)];
        bool Resumed(StrategyPriceBar[] bars, DateTimeOffset now, DateTimeOffset? entry = null) =>
            BankNiftyConfirmedTrail.HasResumedAfterProfitLock(bars, entry ?? OpenedAt,
                lockAt, now, Direction.Buy, 60);
        Assert.True(Resumed(valid, OpenedAt.AddMinutes(4)));
        Assert.False(Resumed(valid, OpenedAt.AddMinutes(4).AddTicks(-1)));
        Assert.False(Resumed([valid[0], valid[2]], OpenedAt.AddMinutes(4)));
        Assert.False(Resumed(valid, OpenedAt.AddMinutes(4), OpenedAt.AddMinutes(1).AddSeconds(1)));
        Assert.False(Resumed([valid[0], valid[1], valid[2] with { Close = 110m }], OpenedAt.AddMinutes(4)));
        Assert.False(Resumed([valid[0], valid[1], valid[2] with { OpenTimeUtc = OpenedAt.AddMinutes(4) }],
            OpenedAt.AddMinutes(5)));
    }

    [Fact]
    public void ConfirmedContextSurvivesSerializationButCannotInventTheNextQuotePair()
    {
        var state = new BankNiftyTrailState(ProfitLockAt: OpenedAt.AddMinutes(1),
            ContinuationAt: OpenedAt.AddMinutes(3), Floor: 300m);
        var restored = System.Text.Json.JsonSerializer.Deserialize<BankNiftyTrailState>(
            System.Text.Json.JsonSerializer.Serialize(state))!;
        var first = Evaluate(restored, 100, 106m);
        Assert.Equal(300m, first.State.Floor);
        Assert.Null(restored.Previous);
        var second = Evaluate(first.State, 102, 106.1m);
        Assert.True(second.State.Floor > 300m);
        Assert.Equal(restored.ContinuationAt, second.State.ContinuationAt);
    }

    private static BankNiftyTrailDecision Evaluate(BankNiftyTrailState state, int second, decimal bid,
        Direction direction = Direction.Buy, decimal? spot = null, int? sourceSecond = null, long size = 150)
    {
        var at = OpenedAt.AddSeconds(120 + second);
        return BankNiftyConfirmedTrail.Evaluate(state, OpenedAt, at, direction, 100m, 10m, 30, 150,
            0m, new(bid, bid + .05m, size, at, OpenedAt.AddSeconds(120 + (sourceSecond ?? second))),
            new(60000m, OpenedAt, OpenedAt), spot is { } price ? new(price, at, at) : null,
            [new(OpenedAt.AddMinutes(-1), 60000m, 60002m, 59998m, 60000m),
             new(OpenedAt.AddMinutes(1), 60000m, 60010m, 59990m, spot ?? 60000m)]);
    }
}
