using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Testcontainers.PostgreSql;
using TradingSystem.Application.Broker;
using TradingSystem.Application.Risk;
using TradingSystem.Domain;
using TradingSystem.Domain.Trading;
using TradingSystem.Infrastructure.Execution;
using TradingSystem.Infrastructure.Persistence;

namespace TradingSystem.IntegrationTests;

public sealed class FeedIndependentLiveEntryTests
{
    private static readonly string[] Markets = ["NIFTY", "SENSEX", "BANK-NIFTY"];
    [DockerFact]
    public async Task UpstoxQuotesCanSubmitAndProtectAtGrowwForAllThreeWithoutBypassingSafety()
    {
        var local = Environment.GetEnvironmentVariable("PAPER_BACKUP_TEST_CONNECTION");
        if (local is not null && !local.StartsWith("Host=127.0.0.1;", StringComparison.Ordinal))
            throw new InvalidOperationException("Only a loopback test database is permitted.");
        await using var container = local is null ? new PostgreSqlBuilder("postgres:18.4-alpine").Build() : null;
        if (container is not null) await container.StartAsync(CancellationToken.None);
        var connection = new NpgsqlConnectionStringBuilder(local ?? container!.GetConnectionString());
        if (local is not null) connection.Database += "_execution";
        var clock = new Clock();
        var arms = new Arms(clock);
        var data = new Data(clock);
        await using var services = new ServiceCollection().AddSingleton<TimeProvider>(clock)
            .AddDbContext<TradingDbContext>(o => o.UseNpgsql(connection.ConnectionString))
            .AddSingleton<ILiveTradingArmService>(arms).AddSingleton<IGrowwReadOnlyGateway>(data)
            .AddSingleton<LiveEntryReadiness>().BuildServiceProvider();
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        await db.Database.EnsureCreatedAsync();
        // The old day's fallback marker must no longer prevent live execution.
        db.ApplicationSettings.Add(new(Guid.NewGuid(), TradingMode.Paper, "PaperDataBackupSession", "\"2026-11-25\"", clock.GetUtcNow()));
        await db.SaveChangesAsync();
        var broker = new ExecutionBroker(clock);
        using var wake = new LiveExecutionWakeSignal();
        using var worker = new AutomaticLiveExecutionService(services.GetRequiredService<IServiceScopeFactory>(),
            broker, wake, Options.Create(new LiveExecutionOptions { BuildEnabled = true }), clock,
            NullLogger<AutomaticLiveExecutionService>.Instance);

        foreach (var market in Markets)
        {
            var (intent, paper) = await CreateAsync(market);
            await worker.SubmitAndProtectAsync(db, intent, CancellationToken.None);
            Assert.Equal("Protected", intent.Status);
            Assert.Equal("Groww", intent.ExecutionBroker);
            Assert.Equal(90m, broker.Stops.Last().StopLoss);
            Assert.Equal(paper.Quantity, broker.Stops.Last().Quantity);
            Assert.Equal(paper.Quantity, broker.Orders.Last().Quantity);
            Assert.Equal("Active", paper.Status);
            Assert.Equal(90m, paper.StopLoss);
            Assert.Equal("Upstox", data.Source);
            Assert.Contains(await db.MarketStrategyAudits.Where(x => x.Outcome == "LiveExecution:EntryDataVerified")
                .Select(x => x.ReasonsJson).ToArrayAsync(), x => x.Contains("Upstox", StringComparison.Ordinal));
        }
        Assert.Equal(3, broker.Orders.Count);
        Assert.Equal(3, broker.Stops.Count);

        data.AgeSeconds = 31;
        var (stale, _) = await CreateAsync("SENSEX");
        await worker.SubmitAndProtectAsync(db, stale, CancellationToken.None);
        Assert.Equal("Rejected", stale.Status);
        Assert.Equal(3, broker.Orders.Count);
        data.AgeSeconds = 0;
        data.MissingDepth = true;
        var (depth, _) = await CreateAsync("BANK-NIFTY");
        await worker.SubmitAndProtectAsync(db, depth, CancellationToken.None);
        Assert.Equal("Rejected", depth.Status);
        Assert.Equal(3, broker.Orders.Count);
        data.MissingDepth = false;

        var (expired, _) = await CreateAsync("NIFTY", old: true);
        await worker.SubmitAndProtectAsync(db, expired, CancellationToken.None);
        Assert.Equal("Rejected", expired.Status);
        Assert.Equal(3, broker.Orders.Count);

        data.OnQuote = () => arms.Armed = false;
        var (disarmed, _) = await CreateAsync("NIFTY");
        await worker.SubmitAndProtectAsync(db, disarmed, CancellationToken.None);
        Assert.Equal("Rejected", disarmed.Status);
        Assert.Equal(3, broker.Orders.Count);
        data.OnQuote = null;
        arms.Armed = true;

        broker.UnknownOutcome = true;
        var (unknown, _) = await CreateAsync("SENSEX");
        await Assert.ThrowsAsync<BrokerOrderOutcomeUnknownException>(() => worker.SubmitAndProtectAsync(db, unknown, CancellationToken.None));
        Assert.Equal("ReconciliationRequired", unknown.Status);
        await Assert.ThrowsAsync<BrokerOrderOutcomeUnknownException>(() => worker.SubmitAndProtectAsync(db, unknown, CancellationToken.None));
        Assert.Equal(4, broker.Orders.Count); // No second submission after an uncertain broker response.
        Assert.Equal(3, broker.Stops.Count);

        async Task<(LiveExecutionIntent, MarketPaperPosition)> CreateAsync(string market, bool old = false)
        {
            var now = clock.GetUtcNow();
            var exchange = market == "SENSEX" ? "BSE" : "NSE";
            var option = new Instrument(Guid.NewGuid(), exchange, market.Replace("-", "", StringComparison.Ordinal) + Guid.NewGuid().ToString("N")[..8] + "CE",
                InstrumentSegment.FuturesAndOptions, InstrumentType.CallOption, now);
            var lot = market == "NIFTY" ? 65 : market == "SENSEX" ? 20 : 30;
            option.UpdateBrokerMetadata(Guid.NewGuid().ToString("N"), option.TradingSymbol, new(2026, 11, 30), 100, lot, .05m);
            var paper = new MarketPaperPosition(Guid.NewGuid(), market.ToLowerInvariant(), option.Id, option.Id,
                "normal", Direction.Buy, lot, 100, 90, 120, old ? now.AddMinutes(-3) : now);
            var intent = new LiveExecutionIntent(Guid.NewGuid(), "MARKET_POSITION", paper.Id, market, option.Id,
                Direction.Buy, lot, 100, 90, 120, "LE" + Guid.NewGuid().ToString("N")[..16], now);
            db.Instruments.Add(option); db.MarketPaperPositions.Add(paper); db.LiveExecutionIntents.Add(intent);
            await db.SaveChangesAsync();
            return (intent, paper);
        }
    }

    private sealed class Clock : TimeProvider
    { public override DateTimeOffset GetUtcNow() => new(2026, 11, 25, 4, 30, 0, TimeSpan.Zero); }
    private sealed class Arms(Clock clock) : ILiveTradingArmService
    {
        public bool Armed = true;
        public Task<LiveTradingArmStatus> GetAsync(CancellationToken token) => Task.FromResult(new LiveTradingArmStatus(true,
            Markets.Select(m => new LiveMarketArmStatus(m, Armed, clock.GetUtcNow().AddHours(-1), "test", "Groww")).ToArray(),
            true, true, ["NIFTY", "SENSEX", "BANK-NIFTY"], null, null, null, null));
        public Task<LiveTradingArmStatus> SetAsync(string market, bool armed, string actor, CancellationToken token) => throw new NotSupportedException();
    }
    private sealed class Data(Clock clock) : IGrowwReadOnlyGateway
    {
        public string Source { get; } = "Upstox";
        public int AgeSeconds;
        public bool MissingDepth;
        public Action? OnQuote;
        public Task<GrowwQuote> GetQuoteAsync(GrowwQuoteRequest request, CancellationToken token)
        {
            OnQuote?.Invoke();
            return Task.FromResult(new GrowwQuote(100, clock.GetUtcNow().AddSeconds(-AgeSeconds).ToUnixTimeMilliseconds(),
                99, MissingDepth ? 0 : 1000, 101, 1000, 100, 10, null) { Source = "Upstox" });
        }
        public Task<GrowwUserProfile> GetUserProfileAsync(CancellationToken token) => throw new NotSupportedException();
        public Task<IReadOnlyList<GrowwPosition>> GetPositionsAsync(string segment, CancellationToken token) => throw new NotSupportedException();
        public Task<IReadOnlyList<GrowwInstrumentRecord>> GetInstrumentMasterAsync(CancellationToken token) => throw new NotSupportedException();
        public Task<GrowwOptionChain> GetOptionChainAsync(GrowwOptionChainRequest request, CancellationToken token) => throw new NotSupportedException();
        public Task<GrowwHistoricalCandles> GetHistoricalCandlesAsync(GrowwHistoricalCandleRequest request, CancellationToken token) => throw new NotSupportedException();
        public Task<IReadOnlyList<DateOnly>> GetHistoricalExpiriesAsync(string exchange, string underlyingSymbol, int year, int? month, CancellationToken token) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> GetHistoricalContractsAsync(string exchange, string underlyingSymbol, DateOnly expiryDate, CancellationToken token) => throw new NotSupportedException();
    }
    private sealed class ExecutionBroker(Clock clock) : ILiveExecutionBrokerGateway
    {
        public string Broker => "Groww";
        public string Account => "primary";
        public TradingMode Mode => TradingMode.Live;
        public bool UnknownOutcome;
        public List<BrokerOrderRequest> Orders { get; } = [];
        public List<BrokerProtectionRequest> Stops { get; } = [];
        public decimal ProtectionLimit(decimal stopLoss) => stopLoss - 1;
        public Task<BrokerOrderSnapshot> SubmitAsync(BrokerOrderRequest request, CancellationToken token)
        {
            Orders.Add(request);
            if (UnknownOutcome) throw new BrokerOrderOutcomeUnknownException(request.ClientReference);
            return Task.FromResult(new BrokerOrderSnapshot(request.ClientReference, "order-" + Orders.Count, request.InstrumentId,
                Direction.Buy, request.Quantity, request.Quantity, 100, OrderState.Filled, clock.GetUtcNow()));
        }
        public Task<BrokerProtectionSnapshot> CreateProtectionAsync(BrokerProtectionRequest request, CancellationToken token)
        { Stops.Add(request); return Task.FromResult(new BrokerProtectionSnapshot(request.ClientReference, "stop-" + Stops.Count, "TRIGGER_PENDING", request.Quantity, request.StopLoss, clock.GetUtcNow())); }
        public Task<IReadOnlyList<BrokerPositionSnapshot>> GetPositionsAsync(CancellationToken token) => Task.FromResult<IReadOnlyList<BrokerPositionSnapshot>>([]);
        public Task<BrokerOrderSnapshot?> GetOrderAsync(string reference, CancellationToken token) => Task.FromResult<BrokerOrderSnapshot?>(null);
        public Task<BrokerOrderSnapshot> CancelAsync(string reference, CancellationToken token) => throw new NotSupportedException();
        public Task<BrokerReconciliationResult> ReconcileAsync(IReadOnlyCollection<ExpectedBrokerPosition> expectedPositions, CancellationToken token) => throw new NotSupportedException();
        public Task CancelProtectionAsync(string id, CancellationToken token) => throw new NotSupportedException();
        public Task<BrokerProtectionSnapshot> ModifyProtectionAsync(string id, int quantity, decimal stopLoss, CancellationToken token) => throw new NotSupportedException();
        public Task<BrokerProtectionSnapshot> ConfirmProtectionAsync(string id, int quantity, decimal stopLoss, CancellationToken token) => throw new NotSupportedException();
        public Task<LiveAccountSnapshot> ReadAccountAsync(CancellationToken token) => throw new NotSupportedException();
        public Task<ExternalExitFill> ReadExternalFillAsync(string id, int expectedQuantity, CancellationToken token) => throw new NotSupportedException();
    }
}
