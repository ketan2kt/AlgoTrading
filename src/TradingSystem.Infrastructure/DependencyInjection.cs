using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using TradingSystem.Application.Auditing;
using TradingSystem.Application.SystemStatus;
using TradingSystem.Infrastructure.Auditing;
using TradingSystem.Infrastructure.Identity;
using TradingSystem.Infrastructure.Persistence;
using TradingSystem.Infrastructure.SystemStatus;
using TradingSystem.Application.Broker;
using TradingSystem.Infrastructure.Broker;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TradingSystem.Infrastructure.Broker.Groww;
using TradingSystem.Application.MarketData;
using TradingSystem.Infrastructure.MarketData;
using TradingSystem.Application.Regime;
using TradingSystem.Application.Strategies;
using TradingSystem.Application.Risk;
using TradingSystem.Application.Execution;
using TradingSystem.Infrastructure.Execution;

namespace TradingSystem.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddTradingSystemInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("TradingDatabase");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "ConnectionStrings:TradingDatabase must be supplied securely.");
        }

        services.AddDbContext<TradingDbContext>(options =>
            options.UseNpgsql(connectionString, npgsql =>
                npgsql.EnableRetryOnFailure(3)));
        services
            .AddOptions<DatabaseInitializationOptions>()
            .Bind(configuration.GetSection(DatabaseInitializationOptions.SectionName));
        services.AddHostedService<DatabaseInitializationService>();
        services
            .AddIdentity<ApplicationUser, IdentityRole<Guid>>(options =>
            {
                options.Password.RequiredLength = 14;
                options.Password.RequireDigit = true;
                options.Password.RequireLowercase = true;
                options.Password.RequireUppercase = true;
                options.Password.RequireNonAlphanumeric = true;
                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
                options.User.RequireUniqueEmail = true;
            })
            .AddEntityFrameworkStores<TradingDbContext>()
            .AddDefaultTokenProviders();
        services
            .AddOptions<IdentityBootstrapOptions>()
            .Bind(configuration.GetSection(IdentityBootstrapOptions.SectionName));
        services.AddHostedService<IdentityBootstrapService>();

        services
            .AddOptions<TradingModeOptions>()
            .Bind(configuration.GetSection(TradingModeOptions.SectionName))
            .Validate(options => options.Mode is not Domain.TradingMode.Live,
                "Live mode is not available in Phase 1.")
            .ValidateOnStart();

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ISystemStatusReader, FoundationSystemStatusReader>();
        services.AddScoped<IAuditWriter, EfAuditWriter>();
        services.AddSingleton<PaperBrokerStateStore>();
        services.TryAddSingleton<IPaperBrokerJournal, EfPaperBrokerJournal>();
        services.AddSingleton<PaperBrokerGateway>();
        services.AddSingleton<IBrokerGateway>(provider =>
        {
            var mode = provider.GetRequiredService<IOptions<TradingModeOptions>>().Value.Mode;
            if (mode != Domain.TradingMode.Paper)
            {
                throw new InvalidOperationException(
                    $"No broker gateway is available for {mode} mode in Phase 3.");
            }

            return provider.GetRequiredService<PaperBrokerGateway>();
        });
        services.AddSingleton<IPaperBrokerControl>(provider =>
        {
            _ = provider.GetRequiredService<IBrokerGateway>();
            return provider.GetRequiredService<PaperBrokerGateway>();
        });
        services.AddHostedService<PaperBrokerRecoveryService>();

        services.AddOptions<GrowwOptions>()
            .Bind(configuration.GetSection(GrowwOptions.SectionName))
            .Validate(options => Uri.TryCreate(options.ApiBaseUrl, UriKind.Absolute, out var api) &&
                                 api.Scheme == Uri.UriSchemeHttps,
                "Groww API base URL must be an absolute HTTPS URL.")
            .Validate(options => Uri.TryCreate(options.InstrumentMasterUrl, UriKind.Absolute, out var instruments) &&
                                 instruments.Scheme == Uri.UriSchemeHttps,
                "Groww instrument URL must be an absolute HTTPS URL.")
            .Validate(options => options.TimeoutSeconds is >= 1 and <= 60,
                "Groww timeout must be between 1 and 60 seconds.")
            .Validate(options => options.MaximumInstrumentBytes is >= 1_000_000 and <= 128 * 1024 * 1024,
                "Groww instrument size limit is invalid.")
            .ValidateOnStart();
        services.AddSingleton<IGrowwAccessTokenProvider, EnvironmentGrowwAccessTokenProvider>();
        services.AddScoped<IGrowwTokenVault, EfGrowwTokenVault>();
        services.AddHttpClient(GrowwReadOnlyGateway.ApiClientName, (provider, client) =>
        {
            var groww = provider.GetRequiredService<IOptions<GrowwOptions>>().Value;
            client.BaseAddress = new Uri(groww.ApiBaseUrl, UriKind.Absolute);
            client.Timeout = TimeSpan.FromSeconds(groww.TimeoutSeconds);
        }).AddStandardResilienceHandler();
        services.AddHttpClient(GrowwReadOnlyGateway.InstrumentClientName, (provider, client) =>
        {
            var groww = provider.GetRequiredService<IOptions<GrowwOptions>>().Value;
            client.Timeout = TimeSpan.FromSeconds(groww.TimeoutSeconds);
        }).AddStandardResilienceHandler();
        services.AddSingleton<IGrowwReadOnlyGateway, GrowwReadOnlyGateway>();
        services.AddScoped<IGrowwInstrumentSynchronizer, GrowwInstrumentSynchronizer>();
        services.AddHostedService<GrowwInstrumentSynchronizationService>();
        services.AddSingleton<MarketDataHealthMonitor>();
        services.AddSingleton<IMarketDataHealthReader>(provider => provider.GetRequiredService<MarketDataHealthMonitor>());
        services.AddSingleton<FuturesFeedHealthMonitor>();
        services.AddSingleton<IFuturesFeedHealthReader>(provider =>
            provider.GetRequiredService<FuturesFeedHealthMonitor>());
        services.AddScoped<IMarketDataPersistence, EfMarketDataPersistence>();
        services.AddOptions<MarketDataOptions>()
            .Bind(configuration.GetSection(MarketDataOptions.SectionName))
            .Validate(options => options.MaximumAgeSeconds is >= 1 and <= 300,
                "Market-data maximum age must be between 1 and 300 seconds.")
            .Validate(options => options.CandleIntervalSeconds is >= 1 and <= 86400,
                "Candle interval must be between 1 second and 1 day.")
            .ValidateOnStart();
        services.AddSingleton(provider =>
        {
            var options = provider.GetRequiredService<IOptions<MarketDataOptions>>().Value;
            return new MarketDataValidator(provider.GetRequiredService<TimeProvider>(),
                TimeSpan.FromSeconds(options.MaximumAgeSeconds));
        });
        services.AddSingleton(provider =>
        {
            var options = provider.GetRequiredService<IOptions<MarketDataOptions>>().Value;
            return new CandleAggregator(options.CandleIntervalSeconds);
        });
        services.AddScoped<MarketDataProcessor>();
        services.AddOptions<LiveNiftyOptions>()
            .Bind(configuration.GetSection(LiveNiftyOptions.SectionName))
            .Validate(options => options.PollIntervalSeconds is >= 1 and <= 30,
                "Live Nifty poll interval must be between 1 and 30 seconds.")
            .Validate(options => options.WorkspaceCandleCount is >= 30 and <= 1500,
                "Live Nifty workspace candle count must be between 30 and 1000.")
            .Validate(options => options.Exchange == "NSE" && options.Segment == "CASH" &&
                                 options.TradingSymbol == "NIFTY",
                "The initial live market workspace is restricted to the NSE NIFTY cash index.")
            .ValidateOnStart();
        services.AddSingleton<GrowwQuoteNormalizer>();
        services.AddSingleton<LiveNiftyFeedState>();
        services.AddSingleton<MultiMarketFeedState>();
        services.AddSingleton<HeroZeroMonitorState>();
        services.AddSingleton<IHeroZeroMonitorReader>(provider => provider.GetRequiredService<HeroZeroMonitorState>());
        services.AddScoped<ITradingWorkspaceReader, EfTradingWorkspaceReader>();
        services.AddScoped<IPaperTradingReportReader, EfPaperTradingReportReader>();
        services.TryAddSingleton<ILiveMarketDataPublisher, NullLiveMarketDataPublisher>();
        services.AddHostedService<GrowwNiftyLiveMarketDataService>();
        services.AddHostedService<GrowwAdditionalMarketDataService>();
        services.AddScoped<GrowwHistoricalCandleImporter>();
        services.AddHostedService<GrowwNiftyFuturesMarketDataService>();
        services.AddOptions<HeroZeroOptions>()
            .Bind(configuration.GetSection(HeroZeroOptions.SectionName))
            .Validate(value => value.ScanIntervalSeconds is >= 15 and <= 300 &&
                               value.TargetPremium > 0 && value.MinimumPremium > 0 &&
                               value.MaximumPremium > value.MinimumPremium &&
                               value.MaximumSpreadPercent is > 0 and <= 30 &&
                               value.MinimumCandidateScore is >= 0.5m and <= 1m &&
                               value.MaximumCombinedPremium > 0 &&
                               value.CombinedStopLossPercent is > 0 and <= 50 &&
                               value.WinnerActivationMultiple is >= 1.25m and <= 5m &&
                               value.WinnerTrailingFraction is > 0 and < 0.5m,
                "Hero Zero paper strategy settings are invalid.")
            .ValidateOnStart();
        services.AddHostedService<HeroZeroPaperTradingService>();
        services.AddOptions<MarketRegimeOptions>()
            .Bind(configuration.GetSection(MarketRegimeOptions.SectionName))
            .Validate(options => options.MinimumDataQuality is >= 0 and <= 1 &&
                                 options.MinimumTradingConfidence is >= 0 and <= 1,
                "Market-regime quality and confidence thresholds must be between 0 and 1.")
            .ValidateOnStart();
        services.AddSingleton(provider => new MarketRegimeEngine(
            provider.GetRequiredService<IOptions<MarketRegimeOptions>>().Value));
        services.AddSingleton<MarketRegimeMonitor>();
        services.AddSingleton<IMarketRegimeReader>(provider => provider.GetRequiredService<MarketRegimeMonitor>());
        services.AddScoped<IMarketRegimePersistence, EfMarketRegimePersistence>();
        services.AddScoped<MarketRegimeService>();
        services.AddOptions<OpeningRangeBreakoutOptions>()
            .Bind(configuration.GetSection("Strategies:OpeningRangeBreakout"))
            .Validate(options => options.RewardToRiskRatio >= 1m &&
                                 options.MaximumTradesPerDay is >= 1 and <= 3,
                "Opening-range strategy settings are invalid.")
            .ValidateOnStart();
        services.AddOptions<PriceActionStrategyOptions>()
            .Bind(configuration.GetSection("Strategies:PriceAction"))
            .Validate(options => options.MinimumScore is >= 0.50m and <= 1m &&
                                 options.RewardToRiskRatio >= 1m &&
                                 options.MaximumTradesPerStrategyPerDay is >= 1 and <= 3 &&
                                 options.MaximumTradesPerDay is >= 1 and <= 3,
                "Price-action strategy settings are invalid.")
            .ValidateOnStart();
        services.AddSingleton<ITradingStrategy>(provider =>
        {
            var priceAction = provider.GetRequiredService<IOptions<PriceActionStrategyOptions>>().Value;
            return new CompositeTradingStrategy([
                new OpeningRangeBreakoutStrategy(
                    provider.GetRequiredService<IOptions<OpeningRangeBreakoutOptions>>().Value),
                new OpeningRangeRetestStrategy(priceAction),
                new VwapTrendPullbackStrategy(priceAction),
                new EmaPullbackContinuationStrategy(priceAction),
                new RangeBreakoutRetestStrategy(priceAction),
                new VwapRejectionReversalStrategy(priceAction),
                new MomentumExpansionStrategy(priceAction)
            ]);
        });
        services.AddOptions<PreliminaryRiskOptions>()
            .Bind(configuration.GetSection("Risk:Preliminary"))
            .Validate(options => options.MaximumRiskPerTrade > 0 && options.MaximumQuantity > 0 &&
                                 options.MaximumTradesPerDay is >= 1 and <= 3,
                "Preliminary risk settings are invalid.")
            .ValidateOnStart();
        services.AddSingleton(provider => new PreliminaryRiskEngine(
            provider.GetRequiredService<IOptions<PreliminaryRiskOptions>>().Value));
        services.AddScoped<PaperTradeLifecycleService>();
        services.AddScoped<IPaperLifecycleAuditStore, EfPaperLifecycleAuditStore>();
        services.AddScoped<IPaperKillSwitchService, EfPaperKillSwitchService>();
        services.AddScoped<IPaperDailyLossOverrideService, EfPaperDailyLossOverrideService>();
        services.AddOptions<AutomatedPaperTradingOptions>()
            .Bind(configuration.GetSection(AutomatedPaperTradingOptions.SectionName))
            .Validate(options => options.EvaluationIntervalSeconds is >= 2 and <= 60 &&
                                 options.NoTradeAuditIntervalMinutes is >= 5 and <= 60 &&
                                 options.MaximumDailyLoss > 0 &&
                                 options.MaximumEntrySlippagePercent is > 0 and <= 1 &&
                                 options.MaximumOptionSpreadPercent is > 0 and <= 10 &&
                                 options.MaximumOptionPremium > 0 &&
                                 options.MinimumOptionVolume >= 0 && options.MinimumOptionOpenInterest >= 0 &&
                                 options.OptionStopLossPercent is > 0 and <= 50 &&
                                   options.OptionRewardToRiskRatio is >= 1 and <= 5 &&
                                   options.MaximumOptionLots is >= 1 and <= 10 &&
                                   options.MaximumOptionDaysToExpiry is >= 1 and <= 31 &&
                                   options.ExpiryDayInTheMoneySteps is >= 0 and <= 3 &&
                                   options.NormalInTheMoneySteps is >= 0 and <= 3 &&
                                   options.OptionStrikeStep > 0 &&
                                   options.BreakEvenTriggerRiskMultiple is > 0 and <= 5 &&
                                   options.TrailingStopRiskMultiple is > 0 and <= 5 &&
                                   options.TargetExtensionMinimumConfidence is >= 0.5m and <= 1m &&
                                   options.ProfitLockTriggerRiskMultiple is > 0 and <= 5 &&
                                   options.ProfitLockRiskMultiple >= 0 &&
                                   options.ProfitLockRiskMultiple < options.ProfitLockTriggerRiskMultiple &&
                                   options.PartialProfitRiskMultiple is > 0 and <= 5 &&
                                   options.PartialExitFraction is > 0 and < 1 &&
                                   options.MinimumReversalStructureStrength is >= 0.5m and <= 1m &&
                                   options.RequiredReversalEvidenceCount is >= 2 and <= 4 &&
                                   options.ReversalProfitLockFraction is > 0 and < 1 &&
                                   options.ExitResearchMaximumDelayMinutes is >= 1 and <= 10 &&
                                   options.MinimumSignalCandles is >= 4 and <= 21 &&
                                   options.MinimumFuturesConfirmationCandles is >= 2 and <= 21 &&
                                   options.MaximumResearchEntriesPerDay is >= 1 and <= 20 &&
                                   options.MaximumConsecutiveLosses is >= 1 and <= 10 &&
                                   options.LossCooldownMinutes is >= 5 and <= 120 &&
                                   options.MaximumResearchEntriesPerDay is >= 1 and <= 20 &&
                                   options.MaximumConsecutiveLosses is >= 1 and <= 10 &&
                                   options.LossCooldownMinutes is >= 5 and <= 120 &&
                                 TimeOnly.TryParseExact(options.EntryWindowStart, "HH:mm", out _) &&
                                 TimeOnly.TryParseExact(options.OpeningRangeEnd, "HH:mm", out _) &&
                                 TimeOnly.TryParseExact(options.EntryCutoff, "HH:mm", out _) &&
                                 TimeOnly.TryParseExact(options.ForcedExit, "HH:mm", out _),
                "Automated paper-trading settings are invalid.")
            .ValidateOnStart();
        services.AddSingleton<PaperAutomationState>();
        services.AddSingleton<IPaperAutomationReader>(provider =>
            provider.GetRequiredService<PaperAutomationState>());
        services.AddHostedService<AutomatedPaperTradingService>();
        services.AddHostedService<MultiMarketPaperTradingService>();

        return services;
    }
}
