using TradingSystem.Application.Broker;
using TradingSystem.Application.MarketData;
using Microsoft.Extensions.DependencyInjection;

namespace TradingSystem.Infrastructure.MarketData;

// Paper engines and pre-entry live quote verification use this data adapter.
// Recorder, account/profile queries and broker order execution remain separate.
internal sealed class PaperMarketDataGateway(IGrowwReadOnlyGateway primary,
    UpstoxPaperDataClient backup, PaperDataSource state, TimeProvider clock) : IGrowwReadOnlyGateway
{
    public string Source => state.Source;
    public string GetSource(string market) => state.GetSource(market);
    public string GetSource(Guid instrumentId) => state.GetSource(instrumentId);
    internal static IGrowwReadOnlyGateway Resolve(IServiceProvider services) =>
        (IGrowwReadOnlyGateway?)services.GetService<PaperMarketDataGateway>() ?? services.GetRequiredService<IGrowwReadOnlyGateway>();
    private async Task<T> ReadAsync<T>(string? market, Func<CancellationToken, Task<T>> groww, Func<Task<T>> upstox, CancellationToken token)
    {
        await state.InitializeAsync(token);
        if (market is null) return await groww(token);
        if (state.GetSource(market) == "Groww")
        {
            try
            {
                using var primaryTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                primaryTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                return await groww(primaryTimeout.Token);
            }
            catch (Exception e) when (!token.IsCancellationRequested && backup.Enabled &&
                e is GrowwApiException or HttpRequestException or TimeoutException or OperationCanceledException or InvalidOperationException)
            {
                var value = await upstox();
                await state.UseBackupAsync(market, token);
                return value;
            }
        }
        try { return await upstox(); }
        catch (Exception e) when (!token.IsCancellationRequested && e is GrowwApiException or HttpRequestException or
            TimeoutException or OperationCanceledException or InvalidDataException)
        {
            // A missing backup contract must not suppress an available primary.
            // Full candle-stream failback is coordinated by the preparation worker.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var value = await groww(timeout.Token);
            if (state.IsReady(market, "Groww")) await state.UsePrimaryAsync(market, token);
            return value;
        }
    }

    public Task<GrowwQuote> GetQuoteAsync(GrowwQuoteRequest request, CancellationToken token) => ReadAsync(Market(request.TradingSymbol), async primaryToken =>
    {
        var quote = await primary.GetQuoteAsync(request, primaryToken);
        var at = GrowwQuoteTimestamp.ToUtc(quote.LastTradeTimeEpochMilliseconds) ?? quote.ObservedAtUtc;
        if (quote.LastPrice <= 0 || request.Segment == "FNO" &&
            (at is null || quote.BidPrice is not > 0 || quote.OfferPrice is null || quote.OfferPrice < quote.BidPrice ||
             quote.BidQuantity is not > 0 || quote.OfferQuantity is not > 0))
            throw new GrowwApiException("Primary derivative price/book is incomplete.", "INCOMPLETE_BOOK");
        if (at is { } timestamp && (timestamp > clock.GetUtcNow() || clock.GetUtcNow() - timestamp > TimeSpan.FromSeconds(30)))
            throw new GrowwApiException("Primary quote is stale.", "STALE_DATA");
        return quote;
    }, () => backup.GetQuoteAsync(request, token), token);
    public Task<GrowwHistoricalCandles> GetHistoricalCandlesAsync(GrowwHistoricalCandleRequest request, CancellationToken token) =>
        ReadAsync(Market(request.GrowwSymbol), primaryToken => primary.GetHistoricalCandlesAsync(request, primaryToken), () => backup.GetHistoryAsync(request, token), token);
    public Task<GrowwOptionChain> GetOptionChainAsync(GrowwOptionChainRequest request, CancellationToken token) =>
        ReadAsync(Market(request.Underlying), primaryToken => primary.GetOptionChainAsync(request, primaryToken), () => backup.GetChainAsync(request, token), token);
    internal static string? Market(string symbol) => symbol.Contains("BANKNIFTY", StringComparison.Ordinal) ? "BANKNIFTY" :
        symbol.Contains("SENSEX", StringComparison.Ordinal) ? "SENSEX" : symbol.Contains("NIFTY", StringComparison.Ordinal) ? "NIFTY" : null;
    public Task<GrowwUserProfile> GetUserProfileAsync(CancellationToken token) => primary.GetUserProfileAsync(token);
    public Task<IReadOnlyList<GrowwPosition>> GetPositionsAsync(string segment, CancellationToken token) => primary.GetPositionsAsync(segment, token);
    public Task<IReadOnlyList<GrowwInstrumentRecord>> GetInstrumentMasterAsync(CancellationToken token) => primary.GetInstrumentMasterAsync(token);
    public Task<IReadOnlyList<DateOnly>> GetHistoricalExpiriesAsync(string exchange, string underlyingSymbol, int year, int? month, CancellationToken token) => primary.GetHistoricalExpiriesAsync(exchange, underlyingSymbol, year, month, token);
    public Task<IReadOnlyList<string>> GetHistoricalContractsAsync(string exchange, string underlyingSymbol, DateOnly expiryDate, CancellationToken token) => primary.GetHistoricalContractsAsync(exchange, underlyingSymbol, expiryDate, token);
}
