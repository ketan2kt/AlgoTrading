using Microsoft.Extensions.Logging.Abstractions;
using TradingSystem.Application.Broker;
using TradingSystem.Infrastructure.MarketData;

namespace TradingSystem.ContractTests;

public sealed class HistoricalQuoteArchiveContractTests : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("sarthi-quote-archive-").FullName;
    private static readonly DateOnly Date = new(2026, 11, 4);
    private static readonly DateTimeOffset At = new(2026, 11, 4, 4, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("NSE","NIFTY26NOV23000CE")]
    [InlineData("BSE","SENSEX26NOV75000PE")]
    [InlineData("NSE","BANKNIFTY26NOV54000CE")]
    public async Task TimelineReadsOnlyHeldOptionReceiptsWithoutRetimingThePrice(string exchange,string symbol)
    {
        using var archive=Archive();
        var id=Guid.NewGuid();
        var sample=Observation() with { Request=new(exchange,"FNO",symbol),ReceivedAtUtc=At,
            Quote=Observation().Quote with {LastTradeTimeEpochMilliseconds=At.AddSeconds(-45).ToUnixTimeMilliseconds()} };
        archive.TryRecord(sample);
        archive.TryRecord(sample with {Id=Guid.NewGuid(),ReceivedAtUtc=At.AddMinutes(1)});
        archive.TryRecord(sample with {Id=Guid.NewGuid(),Request=new(exchange,"CASH",symbol)});
        await archive.FlushBatchAsync(CancellationToken.None);
        var (marks,gaps)=await archive.HeldMarksAsync(Date,[(id,exchange,symbol,At,At.AddMinutes(1))],CancellationToken.None);
        Assert.False(gaps);
        var point=Assert.Single(marks[id]);
        Assert.Equal(At,point.At);
        Assert.Equal(At.AddSeconds(-45),point.SourceAt);
    }

    [Fact]
    public async Task ExistingQuoteResponsePreservesTwoSidedPriceOiAndReceiptTimeAcrossRestart()
    {
        using var archive = Archive();
        var observation = Observation();
        archive.TryRecord(observation);
        await archive.FlushBatchAsync(CancellationToken.None);
        using var restarted = Archive();
        var restored = await restarted.ReadAsync(Date, CancellationToken.None);
        Assert.Equal(observation, Assert.Single(restored.Observations));
        Assert.False(restored.HasCaptureGaps);
    }

    [Fact]
    public async Task FullBufferDoesNotBlockCallerAndItsLossIsPersisted()
    {
        using var archive = Archive(1);
        archive.TryRecord(Observation());
        archive.TryRecord(Observation());
        await archive.FlushBatchAsync(CancellationToken.None);
        using var restarted = Archive();
        var restored = await restarted.ReadAsync(Date, CancellationToken.None);
        Assert.Single(restored.Observations);
        Assert.True(restored.HasCaptureGaps);
    }

    [Fact]
    public async Task CorruptLineDoesNotHideLaterObservationsOrPretendCompleteCapture()
    {
        await File.WriteAllTextAsync(Path.Combine(directory, "2026-11-04.ndjson"), "interrupted-write");
        using var archive = Archive();
        archive.TryRecord(Observation());
        await archive.FlushBatchAsync(CancellationToken.None);
        var restored = await archive.ReadAsync(Date, CancellationToken.None);
        Assert.Single(restored.Observations);
        Assert.True(restored.HasCaptureGaps);
    }

    [Fact]
    public async Task GracefulStopDrainsBufferAndDoesNotCaptureNaturalGasOrBankex()
    {
        using var archive = Archive();
        archive.TryRecord(Observation());
        archive.TryRecord(Observation() with { Request = new("MCX", "COMMODITY", "NATURALGAS") });
        archive.TryRecord(Observation() with { Request = new("BSE", "FNO", "BANKEX") });
        await archive.StopAsync(CancellationToken.None);
        Assert.Single((await archive.ReadAsync(Date, CancellationToken.None)).Observations);
    }

    [Fact]
    public async Task MarketReadDoesNotRetainOtherMarketsAndFailsRatherThanTruncateAtItsMemoryBudget()
    {
        using var archive = new HistoricalQuoteArchive(TimeProvider.System, NullLogger<HistoricalQuoteArchive>.Instance,
            directory, maximumReadObservations: 1);
        archive.TryRecord(Observation());
        archive.TryRecord(Observation() with { Request = new("BSE", "FNO", "SENSEXCE") });
        await archive.FlushBatchAsync(CancellationToken.None);
        Assert.Single((await archive.ReadMarketAsync(Date, "NIFTY", CancellationToken.None)).Observations);
        await Assert.ThrowsAsync<InvalidDataException>(() => archive.ReadAsync(Date, CancellationToken.None));
    }

    [Fact]
    public async Task StorageBudgetPreservesOldFilesAndPersistsAnExplicitGap()
    {
        using var archive = new HistoricalQuoteArchive(TimeProvider.System, NullLogger<HistoricalQuoteArchive>.Instance,
            directory, maximumArchiveBytes: 0);
        archive.TryRecord(Observation());
        await archive.FlushBatchAsync(CancellationToken.None);
        Assert.True(File.Exists(Path.Combine(directory, "2026-11-04.capture-gap")));
        // No fabricated successful sample or silent deletion when space is exhausted.
        Assert.Empty(Directory.GetFiles(directory, "*.gz"));
    }

    [Fact]
    public async Task CompressedBatchesAndHourShardsRoundTripWithoutLosingEarlierMembers()
    {
        using var archive = Archive();
        archive.TryRecord(Observation());
        await archive.FlushBatchAsync(CancellationToken.None);
        archive.TryRecord(Observation() with { ReceivedAtUtc = At.AddSeconds(3) });
        await archive.FlushBatchAsync(CancellationToken.None);
        archive.TryRecord(Observation() with { ReceivedAtUtc = At.AddHours(1) });
        await archive.FlushBatchAsync(CancellationToken.None);
        using var restarted = Archive();
        var result = await restarted.ReadAsync(Date, CancellationToken.None);
        Assert.Equal(3, result.Observations.Count);
        Assert.False(result.HasCaptureGaps);
        Assert.Equal(2, Directory.GetFiles(directory, "*.gz").Length);
    }

    private HistoricalQuoteArchive Archive(int capacity = 10) => new(TimeProvider.System,
        NullLogger<HistoricalQuoteArchive>.Instance, directory, capacity);
    private static HistoricalQuoteObservation Observation() => new(Guid.NewGuid(), At,
        new("NSE", "FNO", "NIFTY26110322500CE"), new(100, At.ToUnixTimeMilliseconds(), 99, 100, 101, 100, 1000, 5000, 10));
    public void Dispose() => Directory.Delete(directory, recursive: true);
}
