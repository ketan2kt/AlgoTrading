using TradingSystem.Domain.Trading;

namespace TradingSystem.Application.Execution;

public sealed record BankNiftyTrailBook(decimal Bid, decimal Ask, long Size,
    DateTimeOffset ReceivedAt, DateTimeOffset SourceAt);
public sealed record BankNiftyTrailSpot(decimal Price, DateTimeOffset ReceivedAt, DateTimeOffset SourceAt);
public sealed record BankNiftyTrailState(BankNiftyTrailBook? Previous = null,
    BankNiftyTrailBook? FirstBreach = null, decimal? Floor = null);
public sealed record BankNiftyTrailDecision(BankNiftyTrailState State, bool Exit = false, bool NeedsSpot = false);

// Application-managed bid-based guard, additional to the existing broker stop.
// The floor is a decision threshold, never a promise of the exit fill price.
public static class BankNiftyConfirmedTrail
{
    public const string ExitReason = "BankNiftyConfirmedTrail";
    private static readonly TimeSpan MaximumBookAge = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaximumSequenceGap = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ConfirmationTime = TimeSpan.FromSeconds(4);
    public static readonly TimeSpan MaximumSpotAge = TimeSpan.FromSeconds(5);

    public static bool Applies(string market, string strategy) =>
        market == "bank-nifty" && strategy == BankNiftyFailedSetupCooldownPolicy.Strategy;

    public static BankNiftyTrailDecision Evaluate(BankNiftyTrailState state, DateTimeOffset openedAt,
        DateTimeOffset now, Direction direction, decimal entry, decimal risk, int lot, int remaining,
        decimal bookedNet, BankNiftyTrailBook? book, BankNiftyTrailSpot? entrySpot = null,
        BankNiftyTrailSpot? currentSpot = null)
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
        if (continuous && state.Previous is { } confirmed)
        {
            var confirmedBid = Math.Min(confirmed.Bid, book.Bid);
            var favourable = confirmedBid - entry;
            if (favourable >= .25m * risk)
            {
                var net = NetAt(confirmedBid);
                if (net > 0m) floor = Math.Max(floor ?? 0m, favourable >= .75m * risk ? .70m * net : 0m);
            }
        }
        var next = state with { Previous = book, Floor = floor, FirstBreach = null };
        if (state.Floor is null || floor is null || NetAt(book.Bid) > floor) return new(next);
        if (floor > 0m) return new(next, Exit: true);

        if (entrySpot is not { Price: > 0m } || currentSpot is not { Price: > 0m } ||
            !Fresh(entrySpot.ReceivedAt, entrySpot.SourceAt, openedAt, MaximumSpotAge) ||
            !Fresh(currentSpot.ReceivedAt, currentSpot.SourceAt, now, MaximumSpotAge))
            return new(next, NeedsSpot: true);
        var adverse = direction == Direction.Buy ? currentSpot.Price < entrySpot.Price : currentSpot.Price > entrySpot.Price;
        if (!adverse) return new(next);

        var first = continuous ? state.FirstBreach : null;
        return first is not null && book.ReceivedAt - first.ReceivedAt >= ConfirmationTime
            ? new(next, Exit: true)
            : new(next with { FirstBreach = first ?? book });
    }

    private static bool Fresh(DateTimeOffset receipt, DateTimeOffset source, DateTimeOffset asOf, TimeSpan maximumAge) =>
        source <= receipt && receipt <= asOf && asOf - source <= maximumAge;
}
