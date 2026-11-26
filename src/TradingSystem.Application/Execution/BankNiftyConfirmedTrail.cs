using TradingSystem.Application.Strategies;
using TradingSystem.Domain.Trading;

namespace TradingSystem.Application.Execution;

public sealed record BankNiftyTrailBook(decimal Bid, decimal Ask, long Size,
    DateTimeOffset ReceivedAt, DateTimeOffset SourceAt);
public sealed record BankNiftyTrailSpot(decimal Price, DateTimeOffset ReceivedAt, DateTimeOffset SourceAt);
public sealed record BankNiftyTrailState(BankNiftyTrailBook? Previous = null,
    BankNiftyTrailBook? FirstBreach = null, decimal? Floor = null,
    DateTimeOffset? ProfitLockAt = null, DateTimeOffset? ContinuationAt = null,
    DateTimeOffset? StructureCheckedAt = null);
public sealed record BankNiftyTrailDecision(BankNiftyTrailState State, bool Exit = false,
    bool NeedsSpot = false, bool NeedsStructure = false);

// Application-managed bid-based guard, additional to the existing broker stop.
// The floor is a decision threshold, never a promise of the exit fill price.
public static class BankNiftyConfirmedTrail
{
    public const string ExitReason = "BankNiftyConfirmedTrail";
    public const string Version = "bank-confirmed-trail-v3";
    private static readonly TimeSpan MaximumBookAge = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaximumSequenceGap = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ConfirmationTime = TimeSpan.FromSeconds(4);
    public static readonly TimeSpan MaximumSpotAge = TimeSpan.FromSeconds(5);

    public static bool Applies(string market, string strategy) =>
        market == "bank-nifty" && strategy == BankNiftyFailedSetupCooldownPolicy.Strategy;

    public static BankNiftyTrailDecision Evaluate(BankNiftyTrailState state, DateTimeOffset openedAt,
        DateTimeOffset now, Direction direction, decimal entry, decimal risk, int lot, int remaining,
        decimal bookedNet, BankNiftyTrailBook? book, BankNiftyTrailSpot? entrySpot = null,
        BankNiftyTrailSpot? currentSpot = null,
        IReadOnlyList<StrategyPriceBar>? completedBars = null, int intervalSeconds = 60)
    {
        if (book is null || book.Bid <= 0m || book.Ask < book.Bid || lot <= 0 || book.Size < lot ||
            remaining < lot || risk <= 0m || entry <= 0m || book.ReceivedAt < openedAt ||
            !Fresh(book.ReceivedAt, book.SourceAt, now, MaximumBookAge))
            return new(state with { Previous = null, FirstBreach = null });
        if (state.Previous is { } previous && (book.SourceAt <= previous.SourceAt || book.ReceivedAt <= previous.ReceivedAt))
            return new(book.SourceAt == previous.SourceAt && book.Bid == previous.Bid
                ? state : state with { FirstBreach = null });

        decimal NetAt(decimal bid) => bookedNet + (bid - entry) * remaining -
            PaperTradingCostModel.CalculateOptionCharges([
                new(PaperOptionTransactionSide.Buy, entry, remaining),
                new(PaperOptionTransactionSide.Sell, bid, remaining)]).Total;
        var continuous = state.Previous is { } prior && book.ReceivedAt - prior.ReceivedAt <= MaximumSequenceGap;
        var floor = state.Floor;
        var profitLockAt = state.ProfitLockAt;
        var continuationAt = state.ContinuationAt;
        var structureCheckedAt = state.StructureCheckedAt;
        var needsStructure = false;
        if (continuous && state.Previous is { } confirmed)
        {
            var confirmedBid = Math.Min(confirmed.Bid, book.Bid);
            var favourable = confirmedBid - entry;
            if (favourable >= .50m * risk)
            {
                profitLockAt ??= now;
                if (continuationAt is null && intervalSeconds is 60 or 300)
                {
                    var interval = TimeSpan.FromSeconds(intervalSeconds);
                    if (completedBars is { Count: > 0 } &&
                        completedBars[^1].OpenTimeUtc + interval <= now &&
                        now - (completedBars[^1].OpenTimeUtc + interval) < interval)
                    {
                        structureCheckedAt = completedBars[^1].OpenTimeUtc + interval;
                        if (HasResumedAfterProfitLock(completedBars, openedAt, profitLockAt.Value,
                                now, direction, intervalSeconds)) continuationAt = now;
                    }
                    needsStructure = continuationAt is null &&
                        (structureCheckedAt is null || now - structureCheckedAt >= interval);
                }
            }
            if (favourable >= .25m * risk)
            {
                var net = NetAt(confirmedBid);
                var protect = favourable >= .75m * risk ||
                    favourable >= .50m * risk && continuationAt is not null;
                if (net > 0m) floor = Math.Max(floor ?? 0m, protect ? .70m * net : 0m);
            }
        }
        var next = state with { Previous = book, Floor = floor, FirstBreach = null,
            ProfitLockAt = profitLockAt, ContinuationAt = continuationAt, StructureCheckedAt = structureCheckedAt };
        if (state.Floor is null || floor is null || NetAt(book.Bid) > floor)
            return new(next, NeedsStructure: needsStructure);
        if (floor > 0m) return new(next, Exit: true);

        if (entrySpot is not { Price: > 0m } || currentSpot is not { Price: > 0m } ||
            !Fresh(entrySpot.ReceivedAt, entrySpot.SourceAt, openedAt, MaximumSpotAge) ||
            !Fresh(currentSpot.ReceivedAt, currentSpot.SourceAt, now, MaximumSpotAge))
            return new(next, NeedsSpot: true);
        var adverse = direction == Direction.Buy ? currentSpot.Price < entrySpot.Price : currentSpot.Price > entrySpot.Price;
        if (!adverse) return new(next);
        // A small retracement through the entry quote is not a broken setup.
        // Only the early zero-net floor needs a completed structural failure;
        // the positive 70% floor above remains immediate and never loosens.
        if (!HasClosedThroughEntryBoundary(completedBars, openedAt, now,
                direction, currentSpot.Price, intervalSeconds)) return new(next);

        var first = continuous ? state.FirstBreach : null;
        return first is not null && book.ReceivedAt - first.ReceivedAt >= ConfirmationTime
            ? new(next, Exit: true)
            : new(next with { FirstBreach = first ?? book });
    }

    // The initial impulse still gets room to retrace. Earlier protection requires
    // a post-lock pullback followed by a CLOSED break of its pre-pullback extreme.
    // No future/entry-minute bars, stitched gaps, wick-only breaks or elapsed-time cutoff.
    public static bool HasResumedAfterProfitLock(IReadOnlyList<StrategyPriceBar>? bars,
        DateTimeOffset openedAt, DateTimeOffset profitLockAt, DateTimeOffset now,
        Direction direction, int intervalSeconds)
    {
        if (bars is null || intervalSeconds is not (60 or 300) || profitLockAt > now) return false;
        var interval = TimeSpan.FromSeconds(intervalSeconds);
        StrategyPriceBar? previous = null;
        decimal? boundary = null;
        foreach (var bar in bars)
        {
            var closedAt = bar.OpenTimeUtc + interval;
            if (bar.OpenTimeUtc < openedAt || closedAt < profitLockAt - interval) continue;
            if (closedAt > now) break;
            if (previous is not null && bar.OpenTimeUtc - previous.OpenTimeUtc != interval)
            {
                boundary = null;
                previous = null;
            }
            var pullback = direction == Direction.Buy ? bar.Close < bar.Open : bar.Close > bar.Open;
            if (closedAt >= profitLockAt && pullback && previous is not null)
                boundary ??= direction == Direction.Buy ? previous.High : previous.Low;
            else if (boundary is { } level &&
                     (direction == Direction.Buy ? bar.Close > level : bar.Close < level)) return true;
            previous = bar;
        }
        return false;
    }

    public static bool HasClosedThroughEntryBoundary(IReadOnlyList<StrategyPriceBar>? bars,
        DateTimeOffset openedAt, DateTimeOffset now, Direction direction,
        decimal currentSpot, int intervalSeconds)
    {
        if (bars is null || bars.Count < 2 || intervalSeconds is not (60 or 300)) return false;
        var interval = TimeSpan.FromSeconds(intervalSeconds);
        var signal = bars.LastOrDefault(b => b.OpenTimeUtc + interval <= openedAt);
        var last = bars[^1];
        if (signal is null || openedAt - (signal.OpenTimeUtc + interval) >= interval ||
            last.OpenTimeUtc < openedAt || last.OpenTimeUtc + interval > now ||
            now - (last.OpenTimeUtc + interval) >= interval || signal.High <= signal.Low)
            return false;
        var buffer = (signal.High - signal.Low) * .10m;
        return direction == Direction.Buy
            ? last.Close < signal.Low - buffer && currentSpot < signal.Low - buffer
            : last.Close > signal.High + buffer && currentSpot > signal.High + buffer;
    }

    private static bool Fresh(DateTimeOffset receipt, DateTimeOffset source, DateTimeOffset asOf, TimeSpan maximumAge) =>
        source <= receipt && receipt <= asOf && asOf - source <= maximumAge;
}
