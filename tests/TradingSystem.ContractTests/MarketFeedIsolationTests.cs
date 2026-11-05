using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradingSystem.Application.MarketData;
using TradingSystem.Infrastructure.MarketData;

namespace TradingSystem.ContractTests;

public sealed class MarketFeedIsolationTests
{
    [Fact]
    public void DashboardRequestsCoalesceWithoutBlockingOtherMarkets()
    {
        var queue = new MarketWorkspaceRefreshQueue();
        for (var i = 0; i < 10_000; i++) queue.Request("nifty");
        queue.Request("sensex");
        queue.Request("bank-nifty");
        foreach (var market in MarketWorkspaceRefreshQueue.Markets)
        {
            Assert.True(queue.Read(market).TryRead(out _));
            Assert.False(queue.Read(market).TryRead(out _));
            queue.Request(market);
            Assert.True(queue.Read(market).TryRead(out _));
        }
    }

    [Fact]
    public async Task HungSensexDashboardDoesNotBlockNiftyOrBankNiftyAndCanStop()
    {
        var reader = new BlockingReader();
        var queue = new MarketWorkspaceRefreshQueue();
        using var services = new ServiceCollection().AddSingleton<ITradingWorkspaceReader>(reader).BuildServiceProvider();
        using var worker = new MarketWorkspaceRefreshService(services.GetRequiredService<IServiceScopeFactory>(),
            queue, Options.Create(new LiveNiftyOptions { Enabled = true }), NullLogger<MarketWorkspaceRefreshService>.Instance);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            queue.Request("sensex");
            await reader.SensexStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            queue.Request("nifty");
            queue.Request("bank-nifty");
            await Task.WhenAll(reader.NiftyStarted.Task, reader.BankStarted.Task).WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await worker.StopAsync(timeout.Token);
        }
    }

    [Theory]
    [InlineData("nifty")]
    [InlineData("sensex")]
    [InlineData("bank-nifty")]
    public void FeedAgesWithoutRenewingItsTimestampAndRecoversOnlyOnSuccess(string market)
    {
        var clock = new MutableClock();
        var single = new LiveNiftyFeedState(clock);
        var multi = new MultiMarketFeedState(clock);
        var at = clock.GetUtcNow();
        void Success() { if (market == "nifty") single.RecordSuccess(clock.GetUtcNow(), clock.GetUtcNow());
            else multi.RecordSuccess(market, clock.GetUtcNow(), clock.GetUtcNow()); }
        LiveNiftyFeedSnapshot Read() => market == "nifty" ? single.GetSnapshot(TimeSpan.FromSeconds(5))
            : multi.GetSnapshot(market, TimeSpan.FromSeconds(5));
        Success();
        Assert.True(Read().IsFresh);
        if (market == "nifty") single.RecordStatus("Disconnected", "Timeout");
        else multi.RecordStatus(market, "Disconnected", "Timeout");
        Assert.False(Read().IsFresh);
        Assert.Equal("Disconnected", Read().Status);
        Success();
        clock.Now = at.AddSeconds(6);
        Assert.False(Read().IsFresh);
        Assert.Equal("DataStale", Read().Status);
        Assert.Equal(at, Read().LastMarketTimestampUtc);
        Success();
        Assert.True(Read().IsFresh);
        Assert.Equal("Connected", Read().Status);
    }

    private sealed class MutableClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 5, 4, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class BlockingReader : ITradingWorkspaceReader
    {
        public TaskCompletionSource SensexStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource NiftyStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource BankStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<TradingWorkspaceSnapshot> GetNiftyAsync(int candleCount, CancellationToken cancellationToken) => GetAsync("nifty", candleCount, cancellationToken);
        public async Task<TradingWorkspaceSnapshot> GetAsync(string market, int candleCount, CancellationToken cancellationToken)
        {
            if (market == "sensex")
            {
                SensexStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            (market == "nifty" ? NiftyStarted : BankStarted).TrySetResult();
            throw new InvalidOperationException("Simulated dashboard read failure; polling must stay independent.");
        }
    }
}
