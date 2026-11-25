using TradingSystem.Application.Broker;

namespace TradingSystem.Infrastructure.Execution;

internal static class LiveEntryQuoteVerifier
{
    internal sealed record Result(bool Allowed, string Reason, string? Source = null, DateTimeOffset? ObservedAtUtc = null);

    internal static Result Evaluate(GrowwQuote quote, DateTimeOffset now)
    {
        var at = GrowwQuoteTimestamp.ToUtc(quote.LastTradeTimeEpochMilliseconds) ?? quote.ObservedAtUtc;
        if (quote.Source is not ("Groww" or "Upstox") || at is null || at > now ||
            now - at > TimeSpan.FromSeconds(30) || quote.LastPrice <= 0 ||
            quote.BidPrice is not > 0 || quote.OfferPrice is null || quote.OfferPrice < quote.BidPrice ||
            quote.BidQuantity is not > 0 || quote.OfferQuantity is not > 0)
            return new(false, "No fresh executable option price/depth from Groww or Upstox.");
        return new(true, "Fresh option price/depth verified.", quote.Source, at);
    }

    internal static async Task<Result> CheckAsync(IGrowwReadOnlyGateway marketData, GrowwQuoteRequest request,
        TimeProvider clock, CancellationToken token)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(12));
            return Evaluate(await marketData.GetQuoteAsync(request, timeout.Token), clock.GetUtcNow());
        }
        catch (Exception error) when (!token.IsCancellationRequested &&
            error is GrowwApiException or HttpRequestException or TimeoutException or OperationCanceledException or InvalidOperationException or InvalidDataException)
        { return new(false, "Option data verification failed on available feeds; no order submitted."); }
    }
}
