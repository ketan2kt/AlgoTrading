using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using TradingSystem.Infrastructure;
using TradingSystem.Infrastructure.MarketData;
using TradingSystem.Application.Broker;

namespace TradingSystem.ContractTests;

public sealed class ReplayRecorderInfrastructureTests
{
    [Theory]
    [InlineData(false, HealthStatus.Unhealthy)]
    [InlineData(true, HealthStatus.Healthy)]
    public async Task CutoverProbeRequiresCaptureEnabled(bool enabled, HealthStatus expected)
    {
        var check = new RecorderActivationHealthCheck(Options.Create(new HistoricalFeedCaptureOptions { Enabled = enabled }),
            new ResearchCapturePulse(), new FixedClock(new DateTimeOffset(2026, 11, 9, 1, 0, 0, TimeSpan.Zero)), new Archive());
        Assert.Equal(expected, (await check.CheckHealthAsync(new HealthCheckContext())).Status);
    }
    [Fact]
    public async Task MarketHoursCutoverRequiresRealSubscriptionAndFreshReceiptsForEveryMarket()
    {
        var now = new DateTimeOffset(2026, 11, 9, 7, 30, 0, TimeSpan.Zero);
        var date = new DateOnly(2026, 11, 9);
        var pulse = new ResearchCapturePulse(); pulse.Starting(date);
        var archive = new Archive();
        var check = new RecorderActivationHealthCheck(Options.Create(new HistoricalFeedCaptureOptions { Enabled = true }), pulse, new FixedClock(now), archive);
        Assert.Equal(HealthStatus.Unhealthy, (await check.CheckHealthAsync(new())).Status);
        pulse.Subscribed(now, 500);
        Assert.Equal(HealthStatus.Unhealthy, (await check.CheckHealthAsync(new())).Status);
        pulse.Received("NIFTY", now); pulse.Received("SENSEX", now);
        Assert.Equal(HealthStatus.Unhealthy, (await check.CheckHealthAsync(new())).Status);
        pulse.Received("BANKNIFTY", now.AddSeconds(-31));
        Assert.Equal(HealthStatus.Unhealthy, (await check.CheckHealthAsync(new())).Status);
        pulse.Received("BANKNIFTY", now);
        Assert.Equal(HealthStatus.Unhealthy, (await check.CheckHealthAsync(new())).Status);
        archive.Health = new(0, true, now, null); // A prior gap stays marked; resumed writes may recover.
        Assert.Equal(HealthStatus.Healthy, (await check.CheckHealthAsync(new())).Status);
        archive.Health = new(0, true, now.AddSeconds(-31), null);
        Assert.Equal(HealthStatus.Unhealthy, (await check.CheckHealthAsync(new())).Status);
        archive.Health = new(0, true, now, null);
        pulse.Failed("HTTP 401 (Unauthorized)");
        Assert.Equal(HealthStatus.Unhealthy, (await check.CheckHealthAsync(new())).Status);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    { public override DateTimeOffset GetUtcNow() => now; }

    private sealed class Archive : IHistoricalQuoteArchive
    {
        public HistoricalQuoteArchiveHealth? Health { get; set; }
        public HistoricalQuoteArchiveHealth? ReadHealth(DateOnly date) => Health;
        public void TryRecord(HistoricalQuoteObservation observation) => throw new NotSupportedException();
        public void MarkCaptureGap(DateOnly date) => throw new NotSupportedException();
        public Task<HistoricalQuoteArchiveDay> ReadAsync(DateOnly date, CancellationToken token) => throw new NotSupportedException();
    }
    private static readonly string[] RecorderWorkers =
        ["GrowwHistoricalFeedCaptureService", "HistoricalAfterMarketBackfillService", "HistoricalPolicyBuildCaptureService"];
    [Theory]
    [InlineData("quotes/2026-11-06-04-30.ndjson.gz", true)]
    [InlineData("quotes/2026-11-06.capture-gap", true)]
    [InlineData("feed/2026-11-06-session.json", true)]
    [InlineData("policies/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/TradingSystem.Application.dll", true)]
    [InlineData("quotes/../../secret.json", false)]
    [InlineData("quotes/2026-11-06.ndjson.gz.pending", false)]
    [InlineData("policies/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/credentials.json", false)]
    [InlineData("protected-keys/keyring.xml", false)]
    [InlineData("/quotes/2026-11-06.ndjson.gz", false)]
    public void ArchiveAllowlistExcludesKeysAndUnfinishedFiles(string path, bool expected) =>
        Assert.Equal(expected, ReplayBlobStorage.Allowed(path));

    [Theory]
    [InlineData("http://example.blob.core.windows.net/replay")]
    [InlineData("https://example.com/replay")]
    [InlineData("https://example.blob.core.windows.net/replay?sig=secret")]
    [InlineData("https://example.blob.core.windows.net/replay/nested")]
    public void StorageConfigurationNeverAcceptsCredentialsOrNonAzureEndpoints(string address) =>
        Assert.Throws<InvalidOperationException>(() => ReplayBlobStorage.ValidateContainerUri(address));

    [Fact]
    public void RecorderExplicitlyRemovesAllTradingHostedServices()
    {
        var services = new ServiceCollection();
        services.AddHostedService<UnexpectedTradingWorker>();
        ReplayRecorderHosting.RestrictToRecorder(services);
        var hosted = services.Where(value => value.ServiceType == typeof(IHostedService)).ToArray();
        Assert.Equal(5, hosted.Length);
        Assert.DoesNotContain(hosted, value => value.ImplementationType == typeof(UnexpectedTradingWorker));
        Assert.All(hosted.Where(value => value.ImplementationType is not null), value =>
            Assert.Contains(value.ImplementationType!.Name, RecorderWorkers));
        Assert.False(ReplayRecorderHosting.IsRecorder(new ConfigurationBuilder().Build()));
        Assert.True(ReplayRecorderHosting.IsRecorder(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string,string?> { ["ApplicationRole"] = "ReplayRecorder" }).Build()));
    }

    private sealed class UnexpectedTradingWorker : BackgroundService
    {
        protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.CompletedTask;
    }
}
