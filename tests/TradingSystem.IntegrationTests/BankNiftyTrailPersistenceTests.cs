using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;
using TradingSystem.Application.Execution;
using TradingSystem.Domain.Trading;
using TradingSystem.Infrastructure.Persistence;

namespace TradingSystem.IntegrationTests;

public sealed class BankNiftyTrailPersistenceTests
{
    [DockerFact]
    public async Task RestartRestoresCommittedContextAndFloorButNotAnUncommittedConfirmation()
    {
        var local = Environment.GetEnvironmentVariable("PAPER_BACKUP_TEST_CONNECTION");
        if (local is not null && !local.StartsWith("Host=127.0.0.1;", StringComparison.Ordinal))
            throw new InvalidOperationException("Only a loopback test database is permitted.");
        await using var container = local is null ? new PostgreSqlBuilder("postgres:18.4-alpine").Build() : null;
        if (container is not null) await container.StartAsync(CancellationToken.None);
        var connection = new NpgsqlConnectionStringBuilder(local ?? container!.GetConnectionString());
        if (local is not null) connection.Database += "_bank_trail";
        var options = new DbContextOptionsBuilder<TradingDbContext>().UseNpgsql(connection.ConnectionString).Options;
        var opened = new DateTimeOffset(2026, 11, 26, 8, 20, 0, TimeSpan.Zero);
        var position = new MarketPaperPosition(Guid.NewGuid(), "bank-nifty", Guid.NewGuid(), Guid.NewGuid(),
            BankNiftyFailedSetupCooldownPolicy.Strategy, Direction.Buy, 150, 100, 90, 120, opened);
        var outcome = $"BankNiftyTrailContext:{position.Id:N}";
        var locked = new BankNiftyTrailState(ProfitLockAt: opened.AddMinutes(2));
        await using (var db = new TradingDbContext(options, TimeProvider.System))
        {
            await db.Database.EnsureCreatedAsync();
            position.RatchetBankNiftyTrailFloor(0);
            db.MarketPaperPositions.Add(position);
            db.MarketStrategyAudits.Add(new(Guid.NewGuid(), position.Market, position.UnderlyingInstrumentId,
                opened.AddMinutes(2), outcome, 0, JsonSerializer.Serialize(locked)));
            await db.SaveChangesAsync();
        }
        await using (var db = new TradingDbContext(options, TimeProvider.System))
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            var tracked = await db.MarketPaperPositions.SingleAsync(p => p.Id == position.Id);
            tracked.RatchetBankNiftyTrailFloor(500);
            db.MarketStrategyAudits.Add(new(Guid.NewGuid(), position.Market, position.UnderlyingInstrumentId,
                opened.AddMinutes(4), outcome, 0,
                JsonSerializer.Serialize(locked with { ContinuationAt = opened.AddMinutes(4) })));
            await db.SaveChangesAsync();
            await transaction.RollbackAsync();
        }
        await using (var db = new TradingDbContext(options, TimeProvider.System))
        {
            var restored = await db.MarketPaperPositions.AsNoTracking().SingleAsync(p => p.Id == position.Id);
            var saved = await db.MarketStrategyAudits.AsNoTracking()
                .Where(a => a.Market == position.Market && a.Outcome == outcome)
                .OrderByDescending(a => a.CandleTimeUtc).Select(a => a.ReasonsJson).FirstAsync();
            var state = JsonSerializer.Deserialize<BankNiftyTrailState>(saved)! with { Floor = restored.BankNiftyTrailFloorNet };
            Assert.Equal(locked.ProfitLockAt, state.ProfitLockAt);
            Assert.Null(state.ContinuationAt);
            Assert.Null(state.Previous);
            Assert.Equal(0m, state.Floor);
            Assert.Equal(90m, restored.StopLoss);
            Assert.Equal(150, restored.RemainingQuantity);
            var now = opened.AddMinutes(5);
            var first = BankNiftyConfirmedTrail.Evaluate(state, opened, now, Direction.Buy,
                100, 10, 30, 150, 0, new(106, 106.05m, 150, now, now));
            Assert.Equal(0m, first.State.Floor);
            Assert.False(first.Exit);
            Assert.Empty(await db.LiveExecutionIntents.ToArrayAsync());
        }
    }
}
