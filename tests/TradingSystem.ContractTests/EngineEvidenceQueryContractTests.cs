using Microsoft.EntityFrameworkCore;
using TradingSystem.Infrastructure.Diagnostics;
using TradingSystem.Infrastructure.Persistence;

namespace TradingSystem.ContractTests;

public sealed class EngineEvidenceQueryContractTests
{
    [Fact]
    public void EvidenceQueryTranslatesMarketAndIstSessionBoundsWithoutDatabaseAccess()
    {
        using var db = new TradingDbContext(new DbContextOptionsBuilder<TradingDbContext>()
            .UseNpgsql("Host=localhost;Database=analysis_query_contract;Username=analysis_contract").Options,
            TimeProvider.System);
        var sql = EfEngineEvidenceReader.Query(db, new DateOnly(2026, 11, 4), "bank-nifty")
            .OrderBy(value => value.OccurredAtUtc).ThenBy(value => value.Id).Take(201).ToQueryString();
        Assert.Contains("EngineEvidence.bank-nifty.", sql);
        Assert.Contains("2026-11-03T18:30:00", sql);
        Assert.Contains("LIMIT", sql);
    }
}
