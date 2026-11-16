using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TradingSystem.Application.Broker;
using TradingSystem.Application.MarketData;
using TradingSystem.Domain.Trading;
using TradingSystem.Infrastructure.Persistence;
using TradingSystem.Infrastructure.Broker;

namespace TradingSystem.Infrastructure.MarketData;

// Warm native candle streams before changing preference. Existing collectors
// keep running, and switching never resets the paper position/risk ledger.
internal sealed partial class PaperDataContinuityService(UpstoxPaperDataClient backup,
    IGrowwReadOnlyGateway primary, PaperDataSource source, IServiceScopeFactory scopes,
    GrowwQuoteNormalizer normalizer, TimeProvider clock, ILogger<PaperDataContinuityService> logger) : BackgroundService
{
    private static readonly string[] Markets = ["NIFTY", "BANKNIFTY", "SENSEX"];
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!backup.Enabled) return;
        await Task.WhenAll(Markets.Select(market => RunMarketAsync(market, stoppingToken)));
    }

    private async Task RunMarketAsync(string market, CancellationToken stoppingToken)
    {
        var recovery = new PaperProviderRecovery();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var prepare = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                prepare.CancelAfter(TimeSpan.FromMinutes(2));
                await source.InitializeAsync(prepare.Token);
                await backup.PrepareAsync(prepare.Token);
                var now = clock.GetUtcNow().ToOffset(TimeSpan.FromHours(5.5));
                if (now.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) &&
                    now.TimeOfDay >= new TimeSpan(9, 0, 0) && now.TimeOfDay <= new TimeSpan(15, 30, 0))
                {
                    var backupReady = await TryWarmAsync(market, true, stoppingToken);
                    if (source.GetSource(market) == "Upstox")
                    {
                        var primaryReady = await TryWarmAsync(market, false, stoppingToken);
                        if (recovery.Observe(primaryReady, clock.GetUtcNow(), backupReady))
                        {
                            await source.UsePrimaryAsync(market, stoppingToken);
                            Switched(logger, market, "Groww");
                            recovery.Reset();
                        }
                    }
                    else recovery.Reset();
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception error) { PreparationFailed(logger, market, error.GetType().Name); }
            await Task.Delay(TimeSpan.FromSeconds(15), clock, stoppingToken);
        }
    }

    private async Task<bool> TryWarmAsync(string market, bool useBackup, CancellationToken stoppingToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(12));
            await WarmAsync(market, useBackup, timeout.Token);
            return true;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            PreparationFailed(logger, market + (useBackup ? "/Upstox" : "/Groww"), error.GetType().Name);
            return false;
        }
    }

    internal async Task WarmAsync(string market, bool useBackup, CancellationToken token)
    {
        var now = clock.GetUtcNow();
        var local = now.ToOffset(TimeSpan.FromHours(5.5));
        var day = DateOnly.FromDateTime(local.DateTime);
        var exchange = market == "SENSEX" ? "BSE" : "NSE";
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        var index = await db.Instruments.AsNoTracking().SingleOrDefaultAsync(x => x.Exchange == exchange &&
            x.Type == InstrumentType.Index && x.TradingSymbol == market && x.IsActive, token);
        var future = await db.Instruments.AsNoTracking().Where(x => x.Exchange == exchange &&
            x.Type == InstrumentType.Future && x.TradingSymbol.StartsWith(market) && x.ExpiryDate >= day && x.IsActive)
            .OrderBy(x => x.ExpiryDate).FirstOrDefaultAsync(token);
        if (index?.GrowwSymbol is null || future?.GrowwSymbol is null)
            throw new InvalidDataException("Paper index/futures identity is unavailable.");
        var start = new DateTimeOffset(local.Date.AddDays(-7).AddHours(9).AddMinutes(15), local.Offset).ToUniversalTime();
        foreach (var instrument in new[] { index, future })
        {
            var request = new GrowwHistoricalCandleRequest(exchange, instrument.Type == InstrumentType.Index ? "CASH" : "FNO",
                instrument.GrowwSymbol!, start, now, "1minute");
            var history = useBackup ? await backup.GetHistoryAsync(request, token) : await primary.GetHistoricalCandlesAsync(request, token);
            if (history.Source != (useBackup ? "Upstox" : "Groww") || history.IntervalInMinutes != 1)
                throw new InvalidDataException("Unexpected paper history provider or interval.");
            await new GrowwHistoricalCandleImporter(db).ImportAsync(instrument, history, now, token);
            if (local.TimeOfDay >= new TimeSpan(9, 17, 0) && !history.Candles.Any(x =>
                GrowwHistoricalCandleImporter.ParseIndiaTimestamp(x.SourceTimestamp) is var at &&
                at.AddMinutes(1) <= now && now - at.AddMinutes(1) <= TimeSpan.FromSeconds(90)))
                throw new InvalidDataException("Paper recovery is awaiting current native candles.");
            var quoteRequest = new GrowwQuoteRequest(exchange, instrument.Type == InstrumentType.Index ? "CASH" : "FNO", instrument.TradingSymbol);
            var quote = useBackup ? await backup.GetQuoteAsync(quoteRequest, token) : await primary.GetQuoteAsync(quoteRequest, token);
            var received = clock.GetUtcNow();
            var observed = GrowwQuoteTimestamp.ToUtc(quote.LastTradeTimeEpochMilliseconds) ?? quote.ObservedAtUtc ?? received;
            if (quote.LastPrice <= 0 || observed > received || received - observed > TimeSpan.FromSeconds(30))
                throw new InvalidDataException("Paper recovery quote is stale.");
            if (source.GetSource(market) != quote.Source)
                await scope.ServiceProvider.GetRequiredService<MarketDataProcessor>().ProcessAsync(
                    normalizer.Normalize(instrument.Id, quote, received), token, aggregateCandles: false);
        }
        if (!useBackup)
        {
            var code = market == "BANKNIFTY" ? "bank-nifty" : market.ToLowerInvariant();
            var contracts = await db.MarketPaperPositions.AsNoTracking().Where(x => x.Market == code && x.Status == "Active")
                .Select(x => x.ExecutionInstrumentId).Distinct().ToArrayAsync(token);
            // Nifty's original paper ledger is separate from MarketPaperPositions.
            // Include its open contracts rather than validating only a fresh ATM pair.
            if (scope.ServiceProvider.GetService<PaperBrokerGateway>() is { } paperBroker)
                contracts = contracts.Concat((await paperBroker.GetPositionsAsync(token))
                    .Where(x => x.Quantity > 0).Select(x => x.InstrumentId)).Distinct().ToArray();
            var active = await db.Instruments.AsNoTracking().Where(x => contracts.Contains(x.Id) &&
                x.Exchange == exchange && x.TradingSymbol.StartsWith(market)).ToListAsync(token);
            if (active.Count == 0)
            {
                var expiry = await db.Instruments.AsNoTracking().Where(x => x.Exchange == exchange && x.IsActive &&
                    x.TradingSymbol.StartsWith(market) && x.Type == InstrumentType.CallOption && x.ExpiryDate > day)
                    .OrderBy(x => x.ExpiryDate).Select(x => x.ExpiryDate).FirstOrDefaultAsync(token);
                if (expiry is null) throw new InvalidDataException("Recovery option expiry unavailable.");
                var chain = await primary.GetOptionChainAsync(new(exchange, market, expiry.Value), token);
                var nearest = chain.Strikes.Where(x => x.Call is not null && x.Put is not null)
                    .MinBy(x => Math.Abs(x.StrikePrice - chain.UnderlyingLastPrice));
                if (nearest is null) throw new InvalidDataException("Recovery option chain unavailable.");
                var symbols = new[] { nearest.Call!.TradingSymbol, nearest.Put!.TradingSymbol };
                active = await db.Instruments.AsNoTracking().Where(x => x.Exchange == exchange && symbols.Contains(x.TradingSymbol)).ToListAsync(token);
                if (active.Count != 2) throw new InvalidDataException("Recovery contract identities unavailable.");
            }
            foreach (var contract in active)
            {
                var quote = await primary.GetQuoteAsync(new(exchange, "FNO", contract.TradingSymbol), token);
                var at = GrowwQuoteTimestamp.ToUtc(quote.LastTradeTimeEpochMilliseconds);
                if (at is null || at > clock.GetUtcNow() || clock.GetUtcNow() - at > TimeSpan.FromSeconds(30) ||
                    quote.BidPrice is not > 0 || quote.OfferPrice < quote.BidPrice || quote.OfferPrice is null ||
                    quote.BidQuantity is not > 0 || quote.OfferQuantity is not > 0)
                    throw new InvalidDataException("Recovery derivative book is missing or stale.");
            }
        }
        source.RecordReady(market, useBackup ? "Upstox" : "Groww");
    }

    [LoggerMessage(LogLevel.Warning, "Paper backup preparation will retry for {market} ({failureType}).")]
    private static partial void PreparationFailed(ILogger logger, string market, string failureType);
    [LoggerMessage(LogLevel.Information, "Paper data for {market} switched to verified {provider}; position state retained.")]
    private static partial void Switched(ILogger logger, string market, string provider);
}

internal sealed class PaperProviderRecovery
{
    private DateTimeOffset? firstHealthy;
    public bool Observe(bool healthy, DateTimeOffset now, bool backupHealthy)
    {
        if (!healthy) { Reset(); return false; }
        firstHealthy ??= now;
        return !backupHealthy || now - firstHealthy >= TimeSpan.FromSeconds(15);
    }
    public void Reset() => firstHealthy = null;
}
