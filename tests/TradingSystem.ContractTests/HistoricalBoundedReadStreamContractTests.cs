using TradingSystem.Infrastructure.Execution;

namespace TradingSystem.ContractTests;

public sealed class HistoricalBoundedReadStreamContractTests
{
    [Fact]
    public async Task ExpandedByteBudgetAppliesToSyncAndAsyncReadsWithoutOwningTheSource()
    {
        using var input = new MemoryStream(new byte[9]);
        using (var limited = new HistoricalBoundedReadStream(input, 8))
        {
            var buffer = new byte[4];
            Assert.Equal(4, limited.Read(buffer));
            Assert.Equal(4, await limited.ReadAsync(buffer.AsMemory()));
            await Assert.ThrowsAsync<InvalidDataException>(async () => { _ = await limited.ReadAsync(buffer.AsMemory()); });
        }
        Assert.True(input.CanRead);
    }
}
