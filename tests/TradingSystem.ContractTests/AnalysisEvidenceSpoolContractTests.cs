using TradingSystem.Infrastructure.Diagnostics;

namespace TradingSystem.ContractTests;

public sealed class AnalysisEvidenceSpoolContractTests : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("sarthi-evidence-test-").FullName;

    [Fact]
    public async Task DatabaseFailureKeepsRecordAndRestartCanRetryOriginalIdAndTimestamp()
    {
        var record = Record();
        var spool = new AnalysisEvidenceSpool(directory);
        await spool.AppendAsync(record, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => spool.FlushAsync(
            (_, _) => throw new InvalidOperationException("Database unavailable"), CancellationToken.None));
        Assert.Single(Directory.EnumerateFiles(directory));

        var saved = new List<AnalysisEvidenceRecord>();
        await new AnalysisEvidenceSpool(directory).FlushAsync((item, _) =>
        {
            saved.Add(item);
            return Task.CompletedTask;
        }, CancellationToken.None);
        Assert.Equal(record, Assert.Single(saved));
        Assert.Empty(Directory.EnumerateFiles(directory));
    }

    [Fact]
    public async Task CompletedPendingWriteIsRecoveredAfterRestart()
    {
        var spool = new AnalysisEvidenceSpool(directory);
        await spool.AppendAsync(Record(), CancellationToken.None);
        var path = Assert.Single(Directory.EnumerateFiles(directory));
        File.Move(path, path + ".pending");
        await new AnalysisEvidenceSpool(directory).RecoverPendingAsync(CancellationToken.None);
        Assert.EndsWith(".json", Assert.Single(Directory.EnumerateFiles(directory)));
    }

    [Fact]
    public async Task CorruptRecordIsReportedAndRetainedWithoutBlockingGoodRecords()
    {
        await File.WriteAllTextAsync(Path.Combine(directory, "000-corrupt.json"), "not-json");
        var spool = new AnalysisEvidenceSpool(directory);
        await spool.AppendAsync(Record(), CancellationToken.None);
        var saved = 0;
        await Assert.ThrowsAsync<AggregateException>(() => spool.FlushAsync((_, _) =>
        {
            saved++;
            return Task.CompletedTask;
        }, CancellationToken.None));
        Assert.Equal(1, saved);
        Assert.Equal("000-corrupt.json.corrupt", Path.GetFileName(Assert.Single(Directory.EnumerateFiles(directory))));
    }

    [Fact]
    public async Task CorruptFirstBatchCannotStarveLaterHealthyRecords()
    {
        for (var index = 0; index < 20; index++)
            await File.WriteAllTextAsync(Path.Combine(directory, $"000-{index:D2}.json"), "not-json");
        var spool = new AnalysisEvidenceSpool(directory);
        await spool.AppendAsync(Record(), CancellationToken.None);
        var saved = 0;
        Task Persist(AnalysisEvidenceRecord _, CancellationToken token)
        {
            saved++;
            return Task.CompletedTask;
        }
        await Assert.ThrowsAsync<AggregateException>(() => spool.FlushAsync(Persist, CancellationToken.None));
        await spool.FlushAsync(Persist, CancellationToken.None);
        Assert.Equal(1, saved);
        Assert.Equal(20, Directory.EnumerateFiles(directory, "*.corrupt").Count());
    }

    [Fact]
    public async Task CorruptPendingWriteDoesNotPreventHealthyRestartRecovery()
    {
        var spool = new AnalysisEvidenceSpool(directory);
        await spool.AppendAsync(Record(), CancellationToken.None);
        var path = Assert.Single(Directory.EnumerateFiles(directory));
        File.Move(path, path + ".pending");
        await File.WriteAllTextAsync(Path.Combine(directory, "000-corrupt.json.pending"), "not-json");
        await Assert.ThrowsAsync<AggregateException>(() => spool.RecoverPendingAsync(CancellationToken.None));
        var saved = 0;
        await spool.FlushAsync((_, _) =>
        {
            saved++;
            return Task.CompletedTask;
        }, CancellationToken.None);
        Assert.Equal(1, saved);
        Assert.Single(Directory.EnumerateFiles(directory, "*.corrupt"));
    }

    [Fact]
    public async Task DuplicateAppendRetainsOneOriginalRecord()
    {
        var spool = new AnalysisEvidenceSpool(directory);
        var record = Record();
        await spool.AppendAsync(record, CancellationToken.None);
        await spool.AppendAsync(record, CancellationToken.None);
        Assert.Single(Directory.EnumerateFiles(directory));
    }

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(directory)) File.Delete(file);
        Directory.Delete(directory);
    }

    private static AnalysisEvidenceRecord Record() => new(Guid.NewGuid(), "Warning",
        "EngineEvidence.nifty.regular", "Actual no-trade reason",
        new DateTimeOffset(2026, 11, 4, 4, 0, 0, TimeSpan.Zero),
        "{\"phase\":\"FuturesConfirmation\",\"count\":0,\"required\":2}");
}
