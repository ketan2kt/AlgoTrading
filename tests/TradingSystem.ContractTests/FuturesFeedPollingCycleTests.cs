using TradingSystem.Infrastructure.MarketData;

namespace TradingSystem.ContractTests;

public sealed class FuturesFeedPollingCycleTests
{
    [Fact]
    public async Task SensexFailureDoesNotSuppressBankNifty()
    {
        var attempted = new List<string>();
        var failures = 0;
        await FuturesFeedPollingCycle.RunAsync(market =>
        {
            attempted.Add(market.Code);
            if (market.Code == "sensex") throw new InvalidOperationException("provider failed");
            return Task.CompletedTask;
        }, _ => failures++, CancellationToken.None);
        Assert.Equal(2, attempted.Count);
        Assert.Equal("sensex", attempted[0]);
        Assert.Equal("bank-nifty", attempted[1]);
        Assert.Equal(1, failures);
    }

    [Fact]
    public async Task CancellationStopsTheCycleRatherThanBecomingProviderFailure()
    {
        using var source = new CancellationTokenSource();
        var failures = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => FuturesFeedPollingCycle.RunAsync(_ =>
        {
            source.Cancel();
            source.Token.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }, _ => failures++, source.Token));
        Assert.Equal(0, failures);
    }
}
