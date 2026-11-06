using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TradingSystem.Infrastructure;
using TradingSystem.Infrastructure.MarketData;

namespace TradingSystem.ContractTests;

public sealed class ReplayRecorderInfrastructureTests
{
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
