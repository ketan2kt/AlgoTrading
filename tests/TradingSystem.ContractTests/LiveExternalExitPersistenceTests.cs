using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using TradingSystem.Domain.Trading;
using TradingSystem.Infrastructure.Persistence;

namespace TradingSystem.ContractTests;

public sealed class LiveExternalExitPersistenceTests
{
    [Fact]
    public void MigrationIsAdditiveAndSnapshotMatchesTheModel()
    {
        using var db = new TradingDbContext(new DbContextOptionsBuilder<TradingDbContext>()
            .UseNpgsql("Host=localhost;Database=offline_model").Options, TimeProvider.System);
        var sql = db.GetService<IMigrator>().GenerateScript("20261111161000_PersistBankNiftyTrailFloor",
            "20261123123000_PersistExternalLiveExitReconciliation");
        Assert.Contains("ADD \"ExternalExitQuantity\" integer NOT NULL DEFAULT 0", sql);
        Assert.Contains("ADD \"ExternalReconciliationPending\" boolean NOT NULL DEFAULT FALSE", sql);
        Assert.Contains("ADD \"ExternalExitEvidenceJson\" text", sql);
        Assert.DoesNotContain("UPDATE ", sql); Assert.DoesNotContain("DROP ", sql);
        Assert.False(db.Database.HasPendingModelChanges());
        var entity = db.Model.FindEntityType(typeof(LiveExecutionIntent))!;
        Assert.False(entity.FindProperty(nameof(LiveExecutionIntent.ExternalReconciliationPending))!.IsNullable);
        Assert.True(entity.FindProperty(nameof(LiveExecutionIntent.ExternalExitEvidenceJson))!.IsNullable);
    }
}
