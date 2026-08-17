using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using TradingSystem.Domain.Common;
using TradingSystem.Domain.Trading;
using TradingSystem.Infrastructure.Identity;

namespace TradingSystem.Infrastructure.Persistence;

public sealed class TradingDbContext(
    DbContextOptions<TradingDbContext> options,
    TimeProvider timeProvider)
    : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>(options)
{
    public DbSet<ApplicationSetting> ApplicationSettings => Set<ApplicationSetting>();
    public DbSet<BrokerConnection> BrokerConnections => Set<BrokerConnection>();
    public DbSet<BrokerAccessTokenSecret> BrokerAccessTokenSecrets => Set<BrokerAccessTokenSecret>();
    public DbSet<Instrument> Instruments => Set<Instrument>();
    public DbSet<Candle> Candles => Set<Candle>();
    public DbSet<PersistedMarketObservation> MarketObservations => Set<PersistedMarketObservation>();
    public DbSet<OptionChainSnapshot> OptionChainSnapshots => Set<OptionChainSnapshot>();
    public DbSet<ExternalMarketSnapshot> ExternalMarketSnapshots => Set<ExternalMarketSnapshot>();
    public DbSet<MarketRegimeSnapshot> MarketRegimeSnapshots => Set<MarketRegimeSnapshot>();
    public DbSet<Strategy> Strategies => Set<Strategy>();
    public DbSet<StrategyVersion> StrategyVersions => Set<StrategyVersion>();
    public DbSet<StrategyConfiguration> StrategyConfigurations => Set<StrategyConfiguration>();
    public DbSet<Signal> Signals => Set<Signal>();
    public DbSet<StrategyEvaluation> StrategyEvaluations => Set<StrategyEvaluation>();
    public DbSet<PaperTradeResult> PaperTradeResults => Set<PaperTradeResult>();
    public DbSet<PaperExitFollowUp> PaperExitFollowUps => Set<PaperExitFollowUp>();
    public DbSet<PaperTradePriceSample> PaperTradePriceSamples => Set<PaperTradePriceSample>();
    public DbSet<RiskDecision> RiskDecisions => Set<RiskDecision>();
    public DbSet<TradingOrder> Orders => Set<TradingOrder>();
    public DbSet<OrderEvent> OrderEvents => Set<OrderEvent>();
    public DbSet<Trade> Trades => Set<Trade>();
    public DbSet<Position> Positions => Set<Position>();
    public DbSet<PositionEvent> PositionEvents => Set<PositionEvent>();
    public DbSet<DailyRisk> DailyRisks => Set<DailyRisk>();
    public DbSet<TradingSession> TradingSessions => Set<TradingSession>();
    public DbSet<ProviderHealth> ProviderHealth => Set<ProviderHealth>();
    public DbSet<SystemEvent> SystemEvents => Set<SystemEvent>();
    public DbSet<PaperBrokerEvent> PaperBrokerEvents => Set<PaperBrokerEvent>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.HasDefaultSchema("trading");
        ConfigureIdentity(builder);
        ConfigureTradingModel(builder);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        PrepareChanges();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        PrepareChanges();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void PrepareChanges()
    {
        var now = timeProvider.GetUtcNow();

        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.Entity is IAppendOnlyEntity &&
                entry.State is EntityState.Modified or EntityState.Deleted)
            {
                throw new InvalidOperationException(
                    $"{entry.Metadata.ClrType.Name} is append-only.");
            }

            if (entry.Entity is MutableEntity mutable &&
                entry.State == EntityState.Modified)
            {
                mutable.MarkUpdated(now);
            }
        }
    }

    private static void ConfigureIdentity(ModelBuilder builder)
    {
        builder.Entity<ApplicationUser>().ToTable("users");
        builder.Entity<IdentityRole<Guid>>().ToTable("roles");
        builder.Entity<IdentityUserRole<Guid>>().ToTable("user_roles");
        builder.Entity<IdentityUserClaim<Guid>>().ToTable("user_claims");
        builder.Entity<IdentityUserLogin<Guid>>().ToTable("user_logins");
        builder.Entity<IdentityRoleClaim<Guid>>().ToTable("role_claims");
        builder.Entity<IdentityUserToken<Guid>>().ToTable("user_tokens");
    }

    private static void ConfigureTradingModel(ModelBuilder builder)
    {
        builder.Entity<DataSnapshot>(entity =>
        {
            entity.UseTpcMappingStrategy();
            entity.HasKey(value => value.Id);
        });

        builder.Entity<ApplicationSetting>(entity =>
        {
            ConfigureMutable(entity, "application_settings");
            entity.Property(value => value.Key).HasMaxLength(160);
            entity.Property(value => value.ValueJson).HasColumnType("jsonb");
            entity.HasIndex(value => new { value.Mode, value.Key }).IsUnique();
        });

        builder.Entity<BrokerConnection>(entity =>
        {
            ConfigureMutable(entity, "broker_connections");
            entity.Property(value => value.Provider).HasMaxLength(80);
            entity.Property(value => value.SecretReference).HasMaxLength(300);
            entity.HasIndex(value => new { value.Mode, value.Provider }).IsUnique();
        });
        builder.Entity<BrokerAccessTokenSecret>(entity =>
        {
            ConfigureMutable(entity, "broker_access_token_secrets");
            entity.Property(value => value.Provider).HasMaxLength(80);
            entity.Property(value => value.ProtectedValue).HasMaxLength(12000);
            entity.Property(value => value.UpdatedBy).HasMaxLength(160);
            entity.HasIndex(value => value.Provider).IsUnique();
        });

        builder.Entity<Instrument>(entity =>
        {
            ConfigureMutable(entity, "instruments");
            entity.Property(value => value.Exchange).HasMaxLength(20);
            entity.Property(value => value.TradingSymbol).HasMaxLength(120);
            entity.Property(value => value.GrowwSymbol).HasMaxLength(180);
            entity.Property(value => value.ExchangeToken).HasMaxLength(80);
            entity.Property(value => value.StrikePrice).HasPrecision(18, 4);
            entity.Property(value => value.TickSize).HasPrecision(18, 4);
            entity.HasIndex(value => new
            {
                value.Exchange,
                value.Segment,
                value.TradingSymbol,
                value.ExpiryDate,
                value.StrikePrice,
                value.Type
            }).IsUnique();
        });

        builder.Entity<Candle>(entity =>
        {
            ConfigureAppendOnly(entity, "candles");
            ConfigurePrices(entity);
            entity.Property(value => value.OpenInterest).HasPrecision(24, 4);
            entity.Property(value => value.Source).HasMaxLength(80);
            entity.HasIndex(value => new
            {
                value.InstrumentId,
                value.OpenTimeUtc,
                value.IntervalSeconds,
                value.Source
            }).IsUnique();
        });
        builder.Entity<PersistedMarketObservation>(entity =>
        {
            ConfigureAppendOnly(entity, "market_observations");
            entity.Property(value => value.Source).HasMaxLength(80);
            entity.Property(value => value.Price).HasPrecision(18, 4);
            entity.Property(value => value.OpenInterest).HasPrecision(24, 4);
            entity.HasIndex(value => new
            {
                value.InstrumentId,
                value.SourceTimestampUtc,
                value.Source
            }).IsUnique();
            entity.HasIndex(value => value.ReceivedAtUtc);
        });

        ConfigureSnapshot<OptionChainSnapshot>(builder, "option_chain_snapshots");
        ConfigureSnapshot<ExternalMarketSnapshot>(builder, "external_market_snapshots");

        builder.Entity<OptionChainSnapshot>(entity =>
            entity.HasIndex(value => new
            {
                value.UnderlyingInstrumentId,
                value.ExpiryDate,
                value.SourceTimestampUtc,
                value.Source
            }).IsUnique());
        builder.Entity<ExternalMarketSnapshot>(entity =>
        {
            entity.Property(value => value.ProviderType).HasMaxLength(80);
            entity.HasIndex(value => new
            {
                value.ProviderType,
                value.SourceTimestampUtc,
                value.Source
            }).IsUnique();
        });

        builder.Entity<MarketRegimeSnapshot>(entity =>
        {
            ConfigureAppendOnly(entity, "market_regime_snapshots");
            entity.Property(value => value.Confidence).HasPrecision(7, 6);
            entity.Property(value => value.DataQuality).HasPrecision(7, 6);
            entity.Property(value => value.ExplanationJson).HasColumnType("jsonb");
            entity.HasIndex(value => new { value.TradingSessionId, value.ObservedAtUtc });
        });

        builder.Entity<Strategy>(entity =>
        {
            ConfigureMutable(entity, "strategies");
            entity.Property(value => value.Code).HasMaxLength(80);
            entity.Property(value => value.DisplayName).HasMaxLength(160);
            entity.HasIndex(value => value.Code).IsUnique();
        });
        builder.Entity<StrategyVersion>(entity =>
        {
            ConfigureAppendOnly(entity, "strategy_versions");
            entity.Property(value => value.Version).HasMaxLength(40);
            entity.Property(value => value.DefinitionHash).HasMaxLength(128);
            entity.HasIndex(value => new { value.StrategyId, value.Version }).IsUnique();
            entity.HasOne<Strategy>().WithMany().HasForeignKey(value => value.StrategyId);
        });
        builder.Entity<StrategyConfiguration>(entity =>
        {
            ConfigureMutable(entity, "strategy_configurations");
            entity.Property(value => value.ParametersJson).HasColumnType("jsonb");
            entity.HasIndex(value => new { value.Mode, value.StrategyVersionId }).IsUnique();
            entity.HasOne<StrategyVersion>().WithMany()
                .HasForeignKey(value => value.StrategyVersionId);
        });

        builder.Entity<Signal>(entity =>
        {
            ConfigureAppendOnly(entity, "signals");
            entity.Property(value => value.ProposedEntry).HasPrecision(18, 4);
            entity.Property(value => value.ProposedStopLoss).HasPrecision(18, 4);
            entity.Property(value => value.ProposedTarget).HasPrecision(18, 4);
            entity.Property(value => value.Confidence).HasPrecision(7, 6);
            entity.Property(value => value.Fingerprint).HasMaxLength(128);
            entity.Property(value => value.ReasonsJson).HasColumnType("jsonb");
            entity.HasIndex(value => value.Fingerprint).IsUnique();
        });
        builder.Entity<StrategyEvaluation>(entity =>
        {
            ConfigureAppendOnly(entity, "strategy_evaluations");
            entity.Property(value => value.StrategyCode).HasMaxLength(80);
            entity.Property(value => value.StrategyVersion).HasMaxLength(40);
            entity.Property(value => value.Outcome).HasMaxLength(80);
            entity.Property(value => value.FailedConditionsJson).HasColumnType("jsonb");
            entity.Property(value => value.OptionSymbol).HasMaxLength(120);
            entity.Property(value => value.OptionType).HasMaxLength(40);
            entity.Property(value => value.CurrentPrice).HasPrecision(18, 4);
            entity.Property(value => value.OpeningRangeHigh).HasPrecision(18, 4);
            entity.Property(value => value.OpeningRangeLow).HasPrecision(18, 4);
            entity.Property(value => value.Vwap).HasPrecision(18, 4);
            entity.Property(value => value.FastEma).HasPrecision(18, 4);
            entity.Property(value => value.SlowEma).HasPrecision(18, 4);
            entity.Property(value => value.AtrPercent).HasPrecision(12, 6);
            entity.Property(value => value.RelativeFuturesVolume).HasPrecision(12, 6);
            entity.Property(value => value.RegimeConfidence).HasPrecision(7, 6);
            entity.Property(value => value.OptionStrike).HasPrecision(18, 4);
            entity.Property(value => value.OptionPremium).HasPrecision(18, 4);
            entity.HasIndex(value => new { value.StrategyCode, value.InstrumentId, value.CandleTimeUtc }).IsUnique();
            entity.HasIndex(value => value.RecordedAtUtc);
        });
        builder.Entity<PaperTradeResult>(entity =>
        {
            ConfigureAppendOnly(entity, "paper_trade_results");
            entity.Property(value => value.TradingSymbol).HasMaxLength(120);
            entity.Property(value => value.ExitReason).HasMaxLength(80);
            entity.Property(value => value.EntryPrice).HasPrecision(18, 4);
            entity.Property(value => value.ExitPrice).HasPrecision(18, 4);
            entity.Property(value => value.GrossPnl).HasPrecision(18, 2);
            entity.Property(value => value.EstimatedCosts).HasPrecision(18, 2);
            entity.Property(value => value.CostBreakdownJson).HasColumnType("jsonb");
            entity.Property(value => value.RealisedPnl).HasPrecision(18, 2);
            entity.HasIndex(value => value.SignalId).IsUnique();
            entity.HasIndex(value => value.ClosedAtUtc);
        });
        builder.Entity<PaperExitFollowUp>(entity =>
        {
            ConfigureAppendOnly(entity, "paper_exit_follow_ups");
            entity.Property(value => value.ObservedOptionPrice).HasPrecision(18, 4);
            entity.Property(value => value.HypotheticalPnlFromEntry).HasPrecision(18, 2);
            entity.Property(value => value.IncrementalPnlAfterExit).HasPrecision(18, 2);
            entity.HasIndex(value => new { value.TradeResultId, value.HorizonMinutes }).IsUnique();
            entity.HasIndex(value => value.ObservedAtUtc);
        });
        builder.Entity<PaperTradePriceSample>(entity =>
        {
            ConfigureAppendOnly(entity, "paper_trade_price_samples");
            entity.Property(value => value.OptionPrice).HasPrecision(18, 4);
            entity.HasIndex(value => new { value.SignalId, value.ObservedMinuteUtc }).IsUnique();
            entity.HasIndex(value => new { value.InstrumentId, value.ObservedAtUtc });
        });
        builder.Entity<RiskDecision>(entity =>
        {
            ConfigureAppendOnly(entity, "risk_decisions");
            entity.Property(value => value.ReasonsJson).HasColumnType("jsonb");
            entity.Property(value => value.SnapshotJson).HasColumnType("jsonb");
            entity.HasIndex(value => value.SignalId).IsUnique();
        });
        builder.Entity<TradingOrder>(entity =>
        {
            ConfigureMutable(entity, "orders");
            entity.Property(value => value.ClientReference).HasMaxLength(80);
            entity.Property(value => value.BrokerOrderId).HasMaxLength(100);
            entity.Property(value => value.AverageFillPrice).HasPrecision(18, 4);
            entity.HasIndex(value => new { value.Mode, value.ClientReference }).IsUnique();
            entity.HasIndex(value => value.BrokerOrderId)
                .IsUnique()
                .HasFilter("\"BrokerOrderId\" IS NOT NULL");
        });
        builder.Entity<OrderEvent>(entity =>
        {
            ConfigureAppendOnly(entity, "order_events");
            entity.Property(value => value.Reason).HasMaxLength(500);
            entity.Property(value => value.BrokerEvidenceJson).HasColumnType("jsonb");
            entity.HasIndex(value => new { value.OrderId, value.OccurredAtUtc });
        });
        builder.Entity<Trade>(entity =>
        {
            ConfigureAppendOnly(entity, "trades");
            entity.Property(value => value.BrokerTradeId).HasMaxLength(100);
            entity.Property(value => value.Price).HasPrecision(18, 4);
            entity.HasIndex(value => value.BrokerTradeId).IsUnique();
        });
        builder.Entity<Position>(entity =>
        {
            ConfigureMutable(entity, "positions");
            entity.Property(value => value.AverageEntryPrice).HasPrecision(18, 4);
            entity.Property(value => value.StopLoss).HasPrecision(18, 4);
            entity.Property(value => value.Target).HasPrecision(18, 4);
            entity.Property(value => value.RealisedPnl).HasPrecision(18, 2);
            entity.HasIndex(value => new { value.Mode, value.InstrumentId, value.State });
        });
        builder.Entity<PositionEvent>(entity =>
        {
            ConfigureAppendOnly(entity, "position_events");
            entity.Property(value => value.Reason).HasMaxLength(500);
            entity.HasIndex(value => new { value.PositionId, value.OccurredAtUtc });
        });

        builder.Entity<DailyRisk>(entity =>
        {
            ConfigureMutable(entity, "daily_risk");
            entity.Property(value => value.RealisedPnl).HasPrecision(18, 2);
            entity.Property(value => value.UnrealisedPnl).HasPrecision(18, 2);
            entity.Property(value => value.RiskConsumed).HasPrecision(18, 2);
            entity.HasIndex(value => new { value.Mode, value.TradingDate }).IsUnique();
        });
        builder.Entity<TradingSession>(entity =>
        {
            ConfigureMutable(entity, "trading_sessions");
            entity.HasIndex(value => new { value.Mode, value.TradingDate }).IsUnique();
        });
        builder.Entity<ProviderHealth>(entity =>
        {
            ConfigureMutable(entity, "provider_health");
            entity.Property(value => value.Provider).HasMaxLength(100);
            entity.Property(value => value.ErrorCode).HasMaxLength(100);
            entity.HasIndex(value => value.Provider).IsUnique();
        });
        builder.Entity<SystemEvent>(entity =>
        {
            ConfigureAppendOnly(entity, "system_events");
            entity.Property(value => value.Severity).HasMaxLength(30);
            entity.Property(value => value.EventType).HasMaxLength(120);
            entity.Property(value => value.Message).HasMaxLength(2000);
            entity.Property(value => value.DetailsJson).HasColumnType("jsonb");
            entity.HasIndex(value => new { value.EventType, value.OccurredAtUtc });
        });
        builder.Entity<PaperBrokerEvent>(entity =>
        {
            ConfigureAppendOnly(entity, "paper_broker_events");
            entity.Property(value => value.EventType).HasMaxLength(40);
            entity.Property(value => value.ClientReference).HasMaxLength(80);
            entity.Property(value => value.PayloadJson).HasColumnType("jsonb");
            entity.HasIndex(value => value.Sequence).IsUnique();
            entity.HasIndex(value => new { value.ClientReference, value.Sequence });
        });
        builder.Entity<AuditLog>(entity =>
        {
            ConfigureAppendOnly(entity, "audit_logs");
            entity.Property(value => value.Actor).HasMaxLength(160);
            entity.Property(value => value.Action).HasMaxLength(120);
            entity.Property(value => value.EntityType).HasMaxLength(160);
            entity.Property(value => value.EntityId).HasMaxLength(160);
            entity.Property(value => value.Reason).HasMaxLength(1000);
            entity.Property(value => value.BeforeJson).HasColumnType("jsonb");
            entity.Property(value => value.AfterJson).HasColumnType("jsonb");
            entity.Property(value => value.CorrelationId).HasMaxLength(100);
            entity.HasIndex(value => new { value.EntityType, value.EntityId, value.OccurredAtUtc });
            entity.HasIndex(value => value.CorrelationId);
        });
    }

    private static void ConfigureMutable<TEntity>(
        Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<TEntity> entity,
        string tableName)
        where TEntity : MutableEntity
    {
        entity.ToTable(tableName);
        entity.HasKey(value => value.Id);
        entity.Property(value => value.ConcurrencyToken).IsConcurrencyToken();
    }

    private static void ConfigureAppendOnly<TEntity>(
        Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<TEntity> entity,
        string tableName)
        where TEntity : Entity, IAppendOnlyEntity
    {
        entity.ToTable(tableName);
        entity.HasKey(value => value.Id);
    }

    private static void ConfigureSnapshot<TEntity>(ModelBuilder builder, string tableName)
        where TEntity : DataSnapshot
    {
        builder.Entity<TEntity>(entity =>
        {
            entity.ToTable(tableName);
            entity.Property(value => value.Source).HasMaxLength(100);
            entity.Property(value => value.PayloadJson).HasColumnType("jsonb");
            entity.Property(value => value.Confidence).HasPrecision(7, 6);
            entity.Property(value => value.ErrorCode).HasMaxLength(100);
        });
    }

    private static void ConfigurePrices(
        Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<Candle> entity)
    {
        entity.Property(value => value.Open).HasPrecision(18, 4);
        entity.Property(value => value.High).HasPrecision(18, 4);
        entity.Property(value => value.Low).HasPrecision(18, 4);
        entity.Property(value => value.Close).HasPrecision(18, 4);
    }
}
