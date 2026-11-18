using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using TradingSystem.Application.Broker;
using TradingSystem.Domain;
using TradingSystem.Domain.Trading;
using TradingSystem.Infrastructure;
using TradingSystem.Infrastructure.Persistence;
using Xunit;

namespace TradingSystem.IntegrationTests;

public sealed class PostgreSqlPersistenceTests
{
    [DockerFact]
    public async Task GrowwTokenRenewalCoordinatesHostsAndRetainsBackoffAfterRestart()
    {
        await using var container = new PostgreSqlBuilder("postgres:18.4-alpine").Build();
        await container.StartAsync(CancellationToken.None);
        var options = new DbContextOptionsBuilder<TradingDbContext>()
            .UseNpgsql(container.GetConnectionString(), pg => pg.EnableRetryOnFailure(3)).Options;
        await using (var migrate = new TradingDbContext(options, TimeProvider.System))
            await migrate.Database.MigrateAsync(CancellationToken.None);
        async Task<TradingSystem.Infrastructure.Broker.Groww.GrowwRenewalClaim> Claim()
        {
            await using var db = new TradingDbContext(options, TimeProvider.System);
            return await new TradingSystem.Infrastructure.Broker.Groww.GrowwTokenRenewalGate(db, TimeProvider.System)
                .ClaimAsync(CancellationToken.None);
        }
        var hosts = await Task.WhenAll(Claim(), Claim());
        var owner = Assert.Single(hosts, c => c.Owner.HasValue).Owner!.Value;
        Assert.Single(hosts, c => !c.Owner.HasValue);
        var retryAfter = DateTimeOffset.UtcNow.AddHours(1);
        await using (var complete = new TradingDbContext(options, TimeProvider.System))
            await new TradingSystem.Infrastructure.Broker.Groww.GrowwTokenRenewalGate(complete, TimeProvider.System)
                .CompleteAsync(owner, false, 429, retryAfter, CancellationToken.None);
        var restarted = await Claim();
        Assert.Null(restarted.Owner);
        Assert.Equal(retryAfter, restarted.NextAttemptAtUtc);
        Assert.Contains("rate-limited", restarted.Reason, StringComparison.Ordinal);
        // An old worker cannot erase a later owner's reservation/backoff.
        await using (var stale = new TradingDbContext(options, TimeProvider.System))
            await new TradingSystem.Infrastructure.Broker.Groww.GrowwTokenRenewalGate(stale, TimeProvider.System)
                .CompleteAsync(Guid.NewGuid(), true, 200, null, CancellationToken.None);
        Assert.Null((await Claim()).Owner);
    }

    private static readonly string[] AcceptedEntryReasons = ["Accepted breakout"];
    [DockerFact]
    public async Task DailyProfitReviewReadsAllThreeMarketsWithoutTradingOrArchiveCalls()
    {
        await using var container = new PostgreSqlBuilder("postgres:18.4-alpine").Build();
        await container.StartAsync(CancellationToken.None);
        await using var provider = CreatePaperProvider(container.GetConnectionString());
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        await db.Database.MigrateAsync(CancellationToken.None);
        var at = new DateTimeOffset(2026, 11, 13, 4, 0, 0, TimeSpan.Zero);
        foreach (var market in new[] { "nifty", "sensex", "bank-nifty" })
        {
            var symbol = market.Replace("-", "", StringComparison.Ordinal).ToUpperInvariant();
            var underlying = new Instrument(Guid.NewGuid(), "NSE", symbol, InstrumentSegment.Cash, InstrumentType.Index, at);
            var option = new Instrument(Guid.NewGuid(), "NSE", symbol + "26NOV100CE", InstrumentSegment.FuturesAndOptions, InstrumentType.CallOption, at);
            db.Instruments.AddRange(underlying, option);
            var position = new MarketPaperPosition(Guid.NewGuid(), market, underlying.Id, option.Id,
                "Core", Direction.Buy, 100, 100, 90, 120, at);
            position.Mark(110, 90, at.AddMinutes(5));
            position.TakePartial(108, 40, 20, at.AddMinutes(6));
            position.Close(104, 30, "FailedSetupExit", at.AddMinutes(10));
            db.MarketPaperPositions.Add(position);
            db.MarketStrategyAudits.Add(new(Guid.NewGuid(), market, underlying.Id, at.AddMinutes(6),
                $"PartialProfit:{position.Id:N}", 1, """{"partialQuantity":40,"price":108}"""));
            db.MarketStrategyAudits.Add(new(Guid.NewGuid(), market, underlying.Id, at, "PaperPositionOpened", 1,
                System.Text.Json.JsonSerializer.Serialize(new { positionId = position.Id, reasons = AcceptedEntryReasons,
                    location = "Prior resistance reclaimed", paperEntryRecordedAtUtc = at.AddSeconds(2) })));
            db.MarketStrategyAudits.Add(new(Guid.NewGuid(), market, underlying.Id, at.AddMinutes(15), $"PostExit5:{position.Id:N}", 0,
                System.Text.Json.JsonSerializer.Serialize(new { positionId = position.Id, horizonMinutes = 5,
                    observedPrice = 110, observedAtUtc = at.AddMinutes(15) })));
            db.MarketStrategyAudits.Add(new(Guid.NewGuid(), market, underlying.Id, at.AddMinutes(5),
                $"ProfitProtection:{position.Id:N}", 0,
                System.Text.Json.JsonSerializer.Serialize(new { positionId = position.Id,
                    effectiveStop = 101m, protectionStage = "ProfitLocked", observedAtUtc = at.AddMinutes(5).AddSeconds(2) })));
            if (market == "bank-nifty")
            {
                // Morning protection must survive the latest-500 display cap.
                for (var i = 0; i < 501; i++)
                    db.MarketStrategyAudits.Add(new(Guid.NewGuid(), market, underlying.Id, at.AddMinutes(20).AddSeconds(i), "NoSignal", 0, "{}"));
                db.MarketStrategyAudits.Add(new(Guid.NewGuid(), market, underlying.Id, at, "EntryTimingRejected", 0, "{}"));
                db.MarketStrategyAudits.Add(new(Guid.NewGuid(), market, underlying.Id, at, "EntryStage:PaperPositionOpened", 0, "{}"));
            }
        }
        await db.SaveChangesAsync(CancellationToken.None);
        var before = await db.MarketStrategyAudits.CountAsync();
        var reader = scope.ServiceProvider.GetRequiredService<TradingSystem.Application.Diagnostics.IDailyTradingDiagnosticsReader>();
        var report = await reader.ReadAsync(new(2026, 11, 13), "all", CancellationToken.None);
        Assert.NotNull(report.ProfitReview);
        Assert.NotNull(report.CapitalSummary);
        Assert.Equal("Verified", report.CapitalSummary.Status);
        Assert.Equal(30000m, report.CapitalSummary.PeakCapitalUsed);
        Assert.Equal(1530m, report.CapitalSummary.NetRealisedPnl);
        Assert.Equal(5.1m, report.CapitalSummary.ReturnPercent);
        Assert.Null(report.Timeline); // no quote-archive load needed for capital
        Assert.Equal(3, report.Summary.AcceptedDecisionCount);
        Assert.Equal(1, report.Summary.RejectedDecisionCount);
        Assert.Equal(501, report.Summary.NoSetupDecisionCount);
        Assert.Equal(1, report.Summary.MirroredDecisionCount);
        Assert.All(report.Trades, t => {
            var receipt = Assert.Single(t.ProtectionReceipts);
            Assert.Equal(101m, receipt.StopPrice);
            Assert.Equal(at.AddMinutes(5).AddSeconds(2), receipt.AtUtc);
        });
        Assert.Equal(3, report.ProfitReview.Markets.Count);
        Assert.All(report.ProfitReview.Markets, m => { Assert.Equal(510m, m.NetPnl); Assert.Equal(440m, m.GrossGivebackSubtotal); });
        Assert.All(report.ProfitReview.Trades, t => {
            Assert.Equal(60, t.FinalExitUnits); Assert.Equal(360m, Assert.Single(t.FollowUps).GrossDifference);
            Assert.Contains("Prior resistance reclaimed", t.Entry.Reasons);
            Assert.Equal(at.AddSeconds(2), t.Entry.RecordedAtUtc);
        });
        Assert.Equal(before, await db.MarketStrategyAudits.CountAsync());
        Assert.Empty(await db.LiveExecutionIntents.ToArrayAsync());
    }

    [DockerFact]
    public async Task MigrationAndAuditPersistenceWorkAgainstPostgreSql()
    {
        await using var container = new PostgreSqlBuilder("postgres:18.4-alpine").Build();
        await container.StartAsync(CancellationToken.None);

        var options = new DbContextOptionsBuilder<TradingDbContext>()
            .UseNpgsql(container.GetConnectionString())
            .Options;
        await using var context = new TradingDbContext(options, TimeProvider.System);
        await context.Database.MigrateAsync(CancellationToken.None);

        var settingId = Guid.NewGuid();
        context.ApplicationSettings.Add(new ApplicationSetting(
            settingId,
            TradingMode.Paper,
            "Risk.MaximumTradesPerDay",
            """{"value":3}""",
            DateTimeOffset.UtcNow));
        context.AuditLogs.Add(new AuditLog(
            Guid.NewGuid(),
            "integration-test",
            "Created",
            nameof(ApplicationSetting),
            settingId.ToString(),
            "persistence verification",
            "{}",
            """{"value":3}""",
            Guid.NewGuid().ToString(),
            DateTimeOffset.UtcNow));
        await context.SaveChangesAsync(CancellationToken.None);

        Assert.Equal(1, await context.ApplicationSettings.CountAsync());
        Assert.Equal(1, await context.AuditLogs.CountAsync());
    }

    [DockerFact]
    public async Task PaperBrokerStateIsReconstructedFromPostgreSqlAfterRestart()
    {
        await using var container = new PostgreSqlBuilder("postgres:18.4-alpine").Build();
        await container.StartAsync(CancellationToken.None);
        await using (var migrationContext = new TradingDbContext(
                         new DbContextOptionsBuilder<TradingDbContext>()
                             .UseNpgsql(container.GetConnectionString())
                             .Options,
                         TimeProvider.System))
        {
            await migrationContext.Database.MigrateAsync(CancellationToken.None);
        }

        var instrumentId = Guid.NewGuid();
        await using (var firstProvider = CreatePaperProvider(container.GetConnectionString()))
        {
            var gateway = firstProvider.GetRequiredService<IBrokerGateway>();
            var control = firstProvider.GetRequiredService<IPaperBrokerControl>();
            await gateway.SubmitAsync(new BrokerOrderRequest(
                "postgres-restart",
                instrumentId,
                Direction.Buy,
                7,
                22500m,
                3), CancellationToken.None);
            await control.ProcessNextFillAsync("postgres-restart", CancellationToken.None);
        }

        await using var restartedProvider = CreatePaperProvider(container.GetConnectionString());
        var restartedGateway = restartedProvider.GetRequiredService<IBrokerGateway>();
        var order = await restartedGateway.GetOrderAsync("postgres-restart", CancellationToken.None);
        var position = Assert.Single(
            await restartedGateway.GetPositionsAsync(CancellationToken.None));

        Assert.NotNull(order);
        Assert.Equal(OrderState.PartiallyFilled, order.State);
        Assert.Equal(3, order.FilledQuantity);
        Assert.Equal(instrumentId, position.InstrumentId);
        Assert.Equal(3, position.Quantity);
    }

    [DockerFact]
    public async Task GrowwTokenIsEncryptedAndCanBeRecoveredAfterRestart()
    {
        await using var container = new PostgreSqlBuilder("postgres:18.4-alpine").Build();
        await container.StartAsync(CancellationToken.None);
        await using (var migrationContext = new TradingDbContext(
                         new DbContextOptionsBuilder<TradingDbContext>()
                             .UseNpgsql(container.GetConnectionString())
                             .Options,
                         TimeProvider.System))
        {
            await migrationContext.Database.MigrateAsync(CancellationToken.None);
        }

        const string accessToken = "groww-integration-token-that-must-not-be-stored-plainly";
        await using var provider = CreatePaperProvider(container.GetConnectionString());
        await using (var scope = provider.CreateAsyncScope())
        {
            var vault = scope.ServiceProvider.GetRequiredService<IGrowwTokenVault>();
            var status = await vault.StoreAsync(
                accessToken,
                "integration-test",
                CancellationToken.None);

            Assert.True(status.IsConfigured);
            Assert.False(status.IsExpired);
        }

        await using (var verificationScope = provider.CreateAsyncScope())
        {
            var context = verificationScope.ServiceProvider.GetRequiredService<TradingDbContext>();
            var persisted = await context.BrokerAccessTokenSecrets.SingleAsync();
            Assert.DoesNotContain(accessToken, persisted.ProtectedValue, StringComparison.Ordinal);
            Assert.NotEmpty(persisted.ProtectedValue);
            Assert.Equal(1, await context.AuditLogs.CountAsync());

            var vault = verificationScope.ServiceProvider.GetRequiredService<IGrowwTokenVault>();
            Assert.Equal(accessToken, await vault.GetValidTokenAsync(CancellationToken.None));
        }
    }

    private static ServiceProvider CreatePaperProvider(string connectionString)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:TradingDatabase"] = connectionString,
                ["Trading:Mode"] = "Paper",
                ["IdentityBootstrap:Enabled"] = "false"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTradingSystemInfrastructure(configuration);
        // These database-only tests deliberately omit the optional archive. A real
        // web host supplies its configuration/environment; this fixture does not.
        foreach (var descriptor in services.Where(d => d.ServiceType.FullName ==
            "TradingSystem.Infrastructure.MarketData.HistoricalQuoteArchive").ToArray())
            services.Remove(descriptor);
        return services.BuildServiceProvider();
    }
}

public sealed class DockerFactAttribute : FactAttribute
{
    public DockerFactAttribute()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("RUN_POSTGRES_TESTS"),
                "true",
                StringComparison.OrdinalIgnoreCase))
        {
            Skip = "Set RUN_POSTGRES_TESTS=true on a host with Docker to run.";
        }
    }
}
