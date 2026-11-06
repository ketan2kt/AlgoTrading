using TradingSystem.Application.Execution;

namespace TradingSystem.UnitTests;

public sealed class HistoricalPortfolioReplayTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 29, 4, 0, 0,
        TimeSpan.Zero);
    private static readonly HistoricalReplayParameters Parameters =
        new(0.10m, 0.15m, 0m, 0m, 5, 0);

    [Fact]
    public void PolicySeesItsOwnPriorSimulatedFillAndUsesNextQuoteAfterTrigger()
    {
        var policy = new ObservePortfolioPolicy();
        var result = HistoricalPortfolioReplay.Run([
            Bar(0), Bar(3), Bar(4)
        ], [Quote(2, 100m), Quote(3, 120m), Quote(4, 115m)], policy,
            Parameters, Start, Start.AddMinutes(6));

        var fill = Assert.Single(result.Trades);
        Assert.Equal(Start.AddMinutes(4), fill.ExitAtUtc);
        Assert.Equal(115m, fill.ExitPrice);
        Assert.Equal("ObservedTarget", fill.ExitReason);
        Assert.Equal(1, result.CandidateCount);
        Assert.Equal(3, policy.Frames);
        Assert.Equal(1, policy.ObservedClosedAtFinalFrame);
    }

    [Fact]
    public void LastPriceTriggeredMarketWaitsForLtpBarrierAndFillsAtLaterBid()
    {
        var result = HistoricalPortfolioReplay.Run([Bar(0)], [
            Quote(2, 100m) with { BidPrice = 99m, OfferPrice = 100m },
            Quote(3, 95m) with { BidPrice = 89m, OfferPrice = 96m },
            Quote(4, 116m) with { BidPrice = 114m, OfferPrice = 117m },
            Quote(5, 115m) with { BidPrice = 113m, OfferPrice = 116m }
        ], new LastPriceTriggerPolicy(), Parameters, Start,
            Start.AddMinutes(6));

        var fill = Assert.Single(result.Trades);
        Assert.Equal("ObservedTarget", fill.ExitReason);
        Assert.Equal(Start.AddMinutes(5), fill.ExitAtUtc);
        Assert.Equal(113m, fill.ExitPrice);
        Assert.Equal("ObservedBid", fill.ExitPriceSource);
    }

    [Fact]
    public void ChainLastPricesRemainLabeledNonExecutableOnFills()
    {
        var result = HistoricalPortfolioReplay.Run([Bar(0)], [
            Quote(2, 100m) with { Source = "GrowwChainReceiptLtp" },
            Quote(3, 120m) with { Source = "GrowwChainReceiptLtp" },
            Quote(4, 115m) with { Source = "GrowwChainReceiptLtp" }
        ], new ObservePortfolioPolicy(), Parameters, Start,
            Start.AddMinutes(6));

        var fill = Assert.Single(result.Trades);
        Assert.Equal("ObservedChainLtpNonExecutable", fill.EntryPriceSource);
        Assert.Equal("ObservedChainLtpNonExecutable", fill.ExitPriceSource);
    }

    [Fact]
    public void GapAfterTriggerDoesNotCreateOptimisticExitOrAnotherEntry()
    {
        var policy = new ObservePortfolioPolicy();
        var result = HistoricalPortfolioReplay.Run([
            Bar(0), Bar(4)
        ], [Quote(2, 100m), Quote(3, 120m), Quote(5, 80m)], policy,
            Parameters, Start, Start.AddMinutes(6));

        Assert.Empty(result.Trades);
        Assert.Equal(1, result.MissingExitQuoteCount);
        Assert.True(result.StaleQuoteCount > 0);
        Assert.Equal(1, result.CandidateCount);
    }

    [Fact]
    public void DrawdownIncludesObservedOpenPositionGivebackBeforeTimeExit()
    {
        var result = HistoricalPortfolioReplay.Run([Bar(0)], [
            Quote(2, 100m), Quote(3, 130m), Quote(4, 100m),
            Quote(5, 100m), Quote(6, 100m), Quote(7, 100m)
        ], new ObservePortfolioPolicy(), new HistoricalReplayParameters(
            0.50m, 2m, 0m, 0m, 5, 0), Start, Start.AddMinutes(8));

        Assert.Single(result.Trades);
        Assert.True(result.MaximumDrawdown > 20m * 75m);
        Assert.True(result.Trades[0].ObservedPeakNetPnl > 2000m);
        Assert.True(result.Trades[0].SampledProfitGiveback > 2000m);
        Assert.Equal(result.Trades[0].SampledProfitGiveback,
            result.TotalSampledProfitGiveback);
    }

    [Fact]
    public void OptionChainIsVisibleOnlyAfterReceiptAndExpiresWhenStale()
    {
        var policy = new ObserveChainPolicy();
        var chains = new[]
        {
            new HistoricalOptionChainFrame(Start.AddMinutes(2),
                Start.AddMinutes(2), new DateOnly(2026, 10, 29),
                [new OptionOpenInterestStrike(22000m, 100m, 90m)])
        };
        HistoricalPortfolioReplay.Run([Bar(0), Bar(2), Bar(5)], [], policy,
            Parameters, Start, Start.AddMinutes(7), chains);

        Assert.Equal([false, true, false], policy.SawChain);
    }

    [Fact]
    public void SameTimeChainsForDifferentMarketsRemainSeparate()
    {
        var policy = new ObserveMarketChainsPolicy();
        HistoricalPortfolioReplay.Run([Bar(0)], [], policy, Parameters,
            Start, Start.AddMinutes(3), [
                new HistoricalOptionChainFrame(Start, Start,
                    new DateOnly(2026, 11, 3),
                    [new OptionOpenInterestStrike(22000m, 100m, 90m)])
                { UnderlyingSymbol = "NIFTY" },
                new HistoricalOptionChainFrame(Start, Start,
                    new DateOnly(2026, 11, 3),
                    [new OptionOpenInterestStrike(73000m, 300m, 280m)])
                { UnderlyingSymbol = "SENSEX" }
            ]);

        Assert.Equal(100m, policy.NiftyOi);
        Assert.Equal(300m, policy.SensexOi);
        Assert.True(policy.LegacyViewSuppressed);
    }

    [Fact]
    public void DuplicateObservationsAreRejectedInsteadOfOrderedArbitrarily()
    {
        var policy = new ObserveChainPolicy();
        Assert.Throws<ArgumentException>(() => HistoricalPortfolioReplay.Run(
            [Bar(0), Bar(0)], [], policy, Parameters, Start,
            Start.AddMinutes(5)));
        Assert.Throws<ArgumentException>(() => HistoricalPortfolioReplay.Run(
            [Bar(0)], [Quote(2, 100m), Quote(2, 101m)], policy,
            Parameters, Start, Start.AddMinutes(5)));
    }

    [Fact]
    public void SimultaneousQuoteAndChainAreNotVisibleToBarDecision()
    {
        var policy = new ObserveAsOfPolicy();
        HistoricalPortfolioReplay.Run([Bar(0)], [Quote(0, 90m), Quote(1, 100m)],
            policy, Parameters, Start, Start.AddMinutes(3), [
                new HistoricalOptionChainFrame(Start, Start,
                    new DateOnly(2026, 10, 29),
                    [new OptionOpenInterestStrike(22000m, 90m, 90m)]),
                new HistoricalOptionChainFrame(Start.AddMinutes(1), Start.AddMinutes(1),
                    new DateOnly(2026, 10, 29),
                    [new OptionOpenInterestStrike(22000m, 100m, 100m)])
            ]);

        Assert.Equal(90m, policy.VisibleQuote);
        Assert.Equal(90m, policy.VisibleChainOi);
    }

    [Fact]
    public void NewestAvailableChainSourceWinsOverLaterStaleReceipt()
    {
        var policy = new ObserveAsOfPolicy();
        HistoricalPortfolioReplay.Run([Bar(0)], [], policy, Parameters,
            Start, Start.AddMinutes(3), [
                new HistoricalOptionChainFrame(Start,
                    Start, new DateOnly(2026, 10, 29),
                    [new OptionOpenInterestStrike(22000m, 100m, 90m)]),
                new HistoricalOptionChainFrame(Start.AddSeconds(30),
                    Start.AddMinutes(-1), new DateOnly(2026, 10, 29),
                    [new OptionOpenInterestStrike(22000m, 50m, 45m)])
            ]);

        Assert.Equal(100m, policy.VisibleChainOi);
    }

    [Fact]
    public void SameTimestampQuoteCannotLeakThroughPositionOrClosedTradeState()
    {
        var policy = new ObservePortfolioPolicy();
        HistoricalPortfolioReplay.Run([Bar(0), Bar(2), Bar(3), Bar(4)], [
            Quote(2, 100m), Quote(3, 120m), Quote(4, 115m)
        ], policy, Parameters, Start, Start.AddMinutes(6));

        Assert.Equal(new decimal?[] { null, 100m, 120m, null },
            policy.ObservedOpenPrices);
        Assert.Equal([0, 0, 0, 1], policy.ObservedClosedCounts);
    }

    [Fact]
    public void RecentlyReceivedButOldSourceChainIsNotFreshEvidence()
    {
        var policy = new ObserveChainPolicy();
        HistoricalPortfolioReplay.Run([Bar(0)], [], policy, Parameters,
            Start, Start.AddMinutes(3), [
                new HistoricalOptionChainFrame(Start, Start.AddMinutes(-4),
                    new DateOnly(2026, 10, 29),
                    [new OptionOpenInterestStrike(22000m, 100m, 90m)])
            ]);

        Assert.Equal([false], policy.SawChain);
    }

    [Fact]
    public void IntraminuteStopAndTargetTieUsesStopAndLaterQuoteFill()
    {
        var optionBar = new HistoricalReplayBar(Start.AddMinutes(2), 60,
            100m, 120m, 80m, 110m) { Symbol = "NIFTY22000CE" };
        var result = HistoricalPortfolioReplay.Run([Bar(0), optionBar], [
            Quote(2, 100m), Quote(3, 110m), Quote(4, 90m)
        ], new ObservePortfolioPolicy(), Parameters, Start, Start.AddMinutes(5));

        var fill = Assert.Single(result.Trades);
        Assert.Equal("AmbiguousIntrabarStop", fill.ExitReason);
        Assert.Equal(Start.AddMinutes(4), fill.ExitAtUtc);
        Assert.Equal(90m, fill.ExitPrice);
    }

    [Fact]
    public void ObservedOfferAndBidControlLongEntryAndExitWhenAvailable()
    {
        var quotes = new[]
        {
            Quote(2, 100m) with { OfferPrice = 102m, BidPrice = 98m },
            Quote(3, 120m) with { OfferPrice = 121m, BidPrice = 118m },
            Quote(4, 116m) with { OfferPrice = 117m, BidPrice = 115m }
        };
        var result = HistoricalPortfolioReplay.Run([Bar(0)], quotes,
            new ObservePortfolioPolicy(), Parameters, Start, Start.AddMinutes(5));

        var fill = Assert.Single(result.Trades);
        Assert.Equal(102m, fill.EntryPrice);
        Assert.Equal(115m, fill.ExitPrice);
        Assert.Equal("ObservedOffer", fill.EntryPriceSource);
        Assert.Equal("ObservedBid", fill.ExitPriceSource);
    }

    [Fact]
    public void CrossedHistoricalQuoteIsRejected()
    {
        Assert.Throws<ArgumentException>(() => HistoricalPortfolioReplay.Run(
            [Bar(0)], [Quote(2, 100m) with { BidPrice = 102m,
                OfferPrice = 101m }], new ObservePortfolioPolicy(), Parameters,
            Start, Start.AddMinutes(5)));
    }

    [Fact]
    public void MinuteStartedBeforeEntryCannotTriggerUnknownPreEntryExtreme()
    {
        var optionBar = new HistoricalReplayBar(Start.AddMinutes(1), 60,
            100m, 120m, 80m, 100m) { Symbol = "NIFTY22000CE" };
        var result = HistoricalPortfolioReplay.Run([Bar(0), optionBar], [
            new HistoricalReplayQuote("NIFTY22000CE",
                Start.AddMinutes(1).AddSeconds(30), 100m),
            Quote(2, 100m), Quote(3, 100m), Quote(4, 100m)
        ], new ObservePortfolioPolicy(), Parameters, Start, Start.AddMinutes(5));

        Assert.Empty(result.Trades);
        Assert.Equal(1, result.MissingExitQuoteCount);
    }

    [Fact]
    public void ExplicitCapacityReplaysTwoConcurrentContractsWithSeparateExits()
    {
        var policy = new ConcurrentPolicy();
        var quotes = new[]
        {
            Quote(2, 100m),
            new HistoricalReplayQuote("NIFTY22000PE", Start.AddMinutes(2), 100m),
            Quote(3, 120m),
            new HistoricalReplayQuote("NIFTY22000PE", Start.AddMinutes(3), 80m),
            Quote(4, 115m),
            new HistoricalReplayQuote("NIFTY22000PE", Start.AddMinutes(4), 85m)
        };
        var result = HistoricalPortfolioReplay.Run([Bar(0), Bar(2)], quotes,
            policy, Parameters with { MaximumConcurrentPositions = 2 },
            Start, Start.AddMinutes(5));

        Assert.Equal(2, policy.OpenSymbolsAtSecondFrame);
        Assert.Equal(2, result.CandidateCount);
        Assert.Equal(2, result.Trades.Count);
        Assert.Equal(["NIFTY22000CE", "NIFTY22000PE"], result.Trades
            .Select(value => value.Symbol).OrderBy(value => value).ToArray());
        Assert.Contains(result.Trades, value => value.ExitReason == "ObservedTarget");
        Assert.Contains(result.Trades, value => value.ExitReason == "ObservedStop");
        Assert.Equal(0, result.MissingEntryQuoteCount);
        Assert.Equal(0, result.MissingExitQuoteCount);

        var defaultCapacity = HistoricalPortfolioReplay.Run([Bar(0)], quotes,
            new ConcurrentPolicy(), Parameters, Start, Start.AddMinutes(5));
        Assert.Empty(defaultCapacity.Trades);
    }

    [Fact]
    public void OnePortfolioClockChargesConcurrentNseAndBseContractsSeparately()
    {
        var symbols = new[] { "NIFTY22000CE", "SENSEX73000PE" };
        var quotes = symbols.SelectMany(symbol => new[]
        {
            new HistoricalReplayQuote(symbol, Start.AddMinutes(2), 100m),
            new HistoricalReplayQuote(symbol, Start.AddMinutes(3), 120m),
            new HistoricalReplayQuote(symbol, Start.AddMinutes(4), 115m)
        }).ToArray();
        var result = HistoricalPortfolioReplay.Run([Bar(0)], quotes,
            new MixedExchangePolicy(), Parameters with
            { MaximumConcurrentPositions = 2 }, Start,
            Start.AddMinutes(5), contractExchanges:
            new Dictionary<string, string>
            {
                [symbols[0]] = "NSE", [symbols[1]] = "BSE"
            });

        Assert.Equal(2, result.CompletedPositions.Count);
        Assert.Equal("NSE", result.Trades.Single(value =>
            value.Symbol == symbols[0]).ExchangeCode);
        Assert.Equal("BSE", result.Trades.Single(value =>
            value.Symbol == symbols[1]).ExchangeCode);
        var expectedCosts = PaperTradingCostModel.CalculateOptionCharges([
            new(PaperOptionTransactionSide.Buy, 100m, 75),
            new(PaperOptionTransactionSide.Sell, 115m, 75)
        ], "NSE").Total + PaperTradingCostModel.CalculateOptionCharges([
            new(PaperOptionTransactionSide.Buy, 100m, 75),
            new(PaperOptionTransactionSide.Sell, 115m, 75)
        ], "BSE").Total;
        Assert.Equal(expectedCosts, result.TotalCosts);
        Assert.Throws<InvalidOperationException>(() =>
            HistoricalPortfolioReplay.Run([Bar(0)], quotes,
                new MixedExchangePolicy(), Parameters with
                { MaximumConcurrentPositions = 2 }, Start,
                Start.AddMinutes(5), contractExchanges:
                new Dictionary<string, string> { [symbols[0]] = "NSE" }));
    }

    [Fact]
    public void SimultaneousOpposingLegQuotesDoNotInventIntratimestampDrawdown()
    {
        var quotes = new[]
        {
            Quote(2, 100m),
            new HistoricalReplayQuote("NIFTY22000PE", Start.AddMinutes(2), 100m),
            Quote(3, 120m),
            new HistoricalReplayQuote("NIFTY22000PE", Start.AddMinutes(3), 80m),
            Quote(4, 115m),
            new HistoricalReplayQuote("NIFTY22000PE", Start.AddMinutes(4), 85m)
        };
        var result = HistoricalPortfolioReplay.Run([Bar(0)], quotes,
            new ConcurrentPolicy(), Parameters with
            { MaximumConcurrentPositions = 2 }, Start, Start.AddMinutes(5));

        Assert.Equal(2, result.Trades.Count);
        Assert.True(result.MaximumDrawdown < 1000m);
        var reversed = HistoricalPortfolioReplay.Run([Bar(0)],
            quotes.Reverse().ToArray(), new ConcurrentPolicy(),
            Parameters with { MaximumConcurrentPositions = 2 }, Start,
            Start.AddMinutes(5));
        Assert.Equal(result.NetPnl, reversed.NetPnl);
        Assert.Equal(result.MaximumDrawdown, reversed.MaximumDrawdown);
    }

    [Fact]
    public void DuplicateContractCandidatesCannotStackInOneReplayFrame()
    {
        var result = HistoricalPortfolioReplay.Run([Bar(0)], [Quote(2, 100m)],
            new ConcurrentPolicy(duplicateSymbol: true),
            Parameters with { MaximumConcurrentPositions = 2 }, Start,
            Start.AddMinutes(4));

        Assert.Empty(result.Trades);
        Assert.Equal(2, result.CandidateCount);
        Assert.Equal(0, result.MissingEntryQuoteCount);
    }

    [Fact]
    public void MissingExitForOneConcurrentLegRemainsAnEvidenceGap()
    {
        var result = HistoricalPortfolioReplay.Run([Bar(0)], [
            Quote(2, 100m),
            new HistoricalReplayQuote("NIFTY22000PE", Start.AddMinutes(2), 100m),
            Quote(3, 120m),
            new HistoricalReplayQuote("NIFTY22000PE", Start.AddMinutes(3), 80m),
            Quote(4, 115m)
        ], new ConcurrentPolicy(),
            Parameters with { MaximumConcurrentPositions = 2 }, Start,
            Start.AddMinutes(5));

        Assert.Single(result.Trades);
        Assert.Equal("NIFTY22000CE", result.Trades[0].Symbol);
        Assert.Equal(1, result.MissingExitQuoteCount);
    }

    [Fact]
    public void AsOfTightenedStopTriggersOnlyOnLaterQuoteAndFillsAfterTrigger()
    {
        var result = HistoricalPortfolioReplay.Run([Bar(0), Bar(2)], [
            Quote(2, 100m), Quote(3, 94m), Quote(4, 93m)
        ], new StopUpdatePolicy(95m), Parameters, Start,
            Start.AddMinutes(5));

        var fill = Assert.Single(result.Trades);
        Assert.Equal("ObservedStop", fill.ExitReason);
        Assert.Equal(Start.AddMinutes(4), fill.ExitAtUtc);
        Assert.Equal(93m, fill.ExitPrice);
        Assert.Equal(90m, fill.InitialStopPrice);
        Assert.Equal(95m, fill.FinalStopPrice);
    }

    [Fact]
    public void QuoteOnlyProtectionCanTightenWithoutAnotherCompletedBar()
    {
        var result = HistoricalPortfolioReplay.Run([Bar(0)], [
            Quote(2, 100m), Quote(3, 130m), Quote(4, 110m),
            Quote(5, 109m)
        ], new StopUpdatePolicy(115m),
            Parameters with { TargetPercent = 2m }, Start,
            Start.AddMinutes(6));

        var fill = Assert.Single(result.Trades);
        Assert.Equal("ObservedStop", fill.ExitReason);
        Assert.Equal(Start.AddMinutes(5), fill.ExitAtUtc);
        Assert.Equal(115m, fill.FinalStopPrice);
    }

    [Fact]
    public void ReplayPolicyCannotLoosenAProtectiveStop()
    {
        Assert.Throws<InvalidOperationException>(() => HistoricalPortfolioReplay.Run(
            [Bar(0), Bar(2)], [Quote(2, 100m)],
            new StopUpdatePolicy(80m), Parameters, Start,
            Start.AddMinutes(5)));
    }

    [Fact]
    public void StaleOptionMarkCannotAuthorizeAProtectiveStopUpdate()
    {
        var result = HistoricalPortfolioReplay.Run([Bar(0), Bar(4)], [
            Quote(2, 100m)
        ], new StopUpdatePolicy(95m, updateAtMinute: 5), Parameters,
            Start, Start.AddMinutes(6));

        Assert.Empty(result.Trades);
        Assert.Equal(1, result.StaleQuoteCount);
        Assert.Equal(1, result.MissingExitQuoteCount);
    }

    [Fact]
    public void LaterPolicyFrameKeepsInitialRiskAfterStopTightens()
    {
        var policy = new StopUpdatePolicy(95m);
        HistoricalPortfolioReplay.Run([Bar(0), Bar(2), Bar(3)], [
            Quote(2, 100m), Quote(3, 100m)
        ], policy, Parameters, Start, Start.AddMinutes(5));

        Assert.Equal(90m, policy.InitialStopAtMinute4);
        Assert.Equal(95m, policy.CurrentStopAtMinute4);
    }

    [Fact]
    public void StopTightenedPastLastVisibleMarkRequestsExitButNeverSameTimeFill()
    {
        var result = HistoricalPortfolioReplay.Run([Bar(0), Bar(2)], [
            Quote(2, 100m), Quote(3, 100m), Quote(4, 97m)
        ], new StopUpdatePolicy(105m), Parameters, Start,
            Start.AddMinutes(5));

        var fill = Assert.Single(result.Trades);
        Assert.Equal("PolicyStop", fill.ExitReason);
        Assert.Equal(Start.AddMinutes(4), fill.ExitAtUtc);
        Assert.Equal(97m, fill.ExitPrice);
    }

    [Fact]
    public void BarCompletingAtSessionEndCannotCreateADeadOnArrivalEntry()
    {
        var result = HistoricalPortfolioReplay.Run([Bar(4)], [],
            new TerminalEntryPolicy(), Parameters, Start,
            Start.AddMinutes(5));

        Assert.Equal(0, result.CandidateCount);
        Assert.Equal(0, result.MissingEntryQuoteCount);
    }

    [Fact]
    public void AsOfPlannedStopAndTargetAreNotRecalculatedFromLaterFill()
    {
        var result = HistoricalPortfolioReplay.Run([Bar(0)], [
            Quote(2, 101m), Quote(3, 126m), Quote(4, 124m)
        ], new PlannedEntryPolicy(), Parameters, Start, Start.AddMinutes(6));

        var fill = Assert.Single(result.Trades);
        Assert.Equal(101m, fill.EntryPrice);
        Assert.Equal(90m, fill.InitialStopPrice);
        Assert.Equal(125m, fill.InitialTargetPrice);
        Assert.Equal(Start.AddMinutes(4), fill.ExitAtUtc);
    }

    [Fact]
    public void VoluntaryExitWaitsForLaterQuoteAndPreservesReason()
    {
        var result = HistoricalPortfolioReplay.Run([Bar(0)], [
            Quote(2, 100m), Quote(3, 105m), Quote(4, 104m)
        ], new VoluntaryExitPolicy(), Parameters, Start, Start.AddMinutes(6));

        var fill = Assert.Single(result.Trades);
        Assert.Equal(Start.AddMinutes(4), fill.ExitAtUtc);
        Assert.Equal(104m, fill.ExitPrice);
        Assert.Equal("ConfirmedReversal", fill.ExitReason);
    }

    [Fact]
    public void ConfirmedRunnerCanPassOriginalTargetButStillExitsAtDeadline()
    {
        var result = HistoricalPortfolioReplay.Run([Bar(0)], [
            Quote(2, 100m), Quote(3, 120m), Quote(4, 121m),
            Quote(5, 122m), Quote(6, 123m), Quote(7, 122m)
        ], new TargetExtensionPolicy(), Parameters,
            Start, Start.AddMinutes(8));

        var fill = Assert.Single(result.Trades);
        Assert.Equal("TimeExit", fill.ExitReason);
        Assert.Equal(Start.AddMinutes(7), fill.ExitAtUtc);
    }

    [Fact]
    public void NewProtectiveStopPrecedesTargetTouchedOnSameQuote()
    {
        var result = HistoricalPortfolioReplay.Run([Bar(0)], [
            Quote(2, 100m), Quote(3, 120m), Quote(4, 119m)
        ], new StopUpdatePolicy(125m), Parameters,
            Start, Start.AddMinutes(6));

        var fill = Assert.Single(result.Trades);
        Assert.Equal("PolicyStop", fill.ExitReason);
        Assert.Equal(Start.AddMinutes(4), fill.ExitAtUtc);
    }

    [Fact]
    public void DeferredAdmissionUsesLaterQuoteThenRequiresAnotherQuoteForFill()
    {
        var result = HistoricalPortfolioReplay.Run([Bar(0)], [
            Quote(2, 100m), Quote(3, 101m),
            Quote(4, 120m), Quote(5, 119m)
        ], new DeferredEntryPolicy(), Parameters,
            Start, Start.AddMinutes(7));

        var fill = Assert.Single(result.Trades);
        Assert.Equal(Start.AddMinutes(1), fill.DecisionAtUtc);
        Assert.Equal(Start.AddMinutes(3), fill.EntryAtUtc);
        Assert.Equal(101m, fill.EntryPrice);
        Assert.Equal(90m, fill.InitialStopPrice);
        Assert.Equal(115m, fill.InitialTargetPrice);
        Assert.Equal(Start.AddMinutes(5), fill.ExitAtUtc);
    }

    [Fact]
    public void NonNiftyDeferredAdmissionAlsoRequiresAThirdQuoteForFill()
    {
        var result = HistoricalPortfolioReplay.Run([Bar(0)], [
            new HistoricalReplayQuote("BANKNIFTY55000CE", Start.AddMinutes(2), 100m)
            { BidPrice = 99m, OfferPrice = 100m },
            new HistoricalReplayQuote("BANKNIFTY55000CE", Start.AddMinutes(3), 101m)
            { BidPrice = 100m, OfferPrice = 102m },
            new HistoricalReplayQuote("BANKNIFTY55000CE", Start.AddMinutes(4), 120m)
            { BidPrice = 119m, OfferPrice = 121m },
            new HistoricalReplayQuote("BANKNIFTY55000CE", Start.AddMinutes(5), 119m)
            { BidPrice = 118m, OfferPrice = 120m }
        ], new GenericDeferredEntryPolicy(), Parameters,
            Start, Start.AddMinutes(7));

        var fill = Assert.Single(result.Trades);
        Assert.Equal(Start.AddMinutes(1), fill.DecisionAtUtc);
        Assert.Equal(Start.AddMinutes(3), fill.EntryAtUtc);
        Assert.Equal(102m, fill.EntryPrice);
        Assert.Equal(90m, fill.InitialStopPrice);
        Assert.Equal(115m, fill.InitialTargetPrice);
        Assert.Equal(Start.AddMinutes(5), fill.ExitAtUtc);
    }

    [Fact]
    public void NonNiftyDeferredAdmissionCannotFillOnItsAdmissionQuote()
    {
        var result = HistoricalPortfolioReplay.Run([Bar(0)], [
            new HistoricalReplayQuote("BANKNIFTY55000CE", Start.AddMinutes(2), 100m)
            { BidPrice = 99m, OfferPrice = 100m }
        ], new GenericDeferredEntryPolicy(), Parameters,
            Start, Start.AddMinutes(7));

        Assert.Empty(result.Trades);
        Assert.Empty(result.CompletedPositions);
    }

    [Fact]
    public void QuoteArrivingAfterProductionAdmissionDeadlineIsAMissingDataOutcomeNotStrategyRejection()
    {
        var result = HistoricalPortfolioReplay.Run([Bar(0)], [
            new HistoricalReplayQuote("BANKNIFTY55000CE", Start.AddMinutes(1).AddSeconds(32), 100m)
            { BidPrice = 99m, OfferPrice = 100m }
        ], new GenericDeferredEntryPolicy(5), Parameters, Start, Start.AddMinutes(7));
        Assert.Empty(result.Trades);
        Assert.Equal(1, result.MissingEntryQuoteCount);
        Assert.Equal(1, result.StaleQuoteCount);
    }

    [Fact]
    public void RecordedEmptyBookNeverFillsAtLastPrice()
    {
        var result = HistoricalPortfolioReplay.Run([Bar(0)], [
            Quote(2, 100) with { Source = "GrowwFeedSample", DepthSourceAtUtc = Start.AddMinutes(2) },
            Quote(3, 120) with { Source = "GrowwFeedSample", DepthSourceAtUtc = Start.AddMinutes(3) }
        ], new ObservePortfolioPolicy(), Parameters, Start, Start.AddMinutes(6));
        Assert.Empty(result.Trades);
        Assert.Equal(1, result.MissingEntryQuoteCount);
    }

    private sealed class GenericDeferredEntryPolicy(int? maximumDelay = null) : IHistoricalReplayPortfolioPolicy
    {
        public string CodeVersion => "generic-deferred-test-v1";
        public IReadOnlyList<HistoricalReplayCandidate> Evaluate(
            HistoricalReplayPortfolioFrame frame) =>
            frame.Market.AvailableAtUtc == Start.AddMinutes(1)
                ? [new HistoricalReplayCandidate("BANKNIFTY55000CE",
                    frame.Market.AvailableAtUtc, CodeVersion, .8m, 30)
                { RequiresDeferredQuoteAdmission = true,
                  MaximumAdmissionQuoteDelaySeconds = maximumDelay,
                  StrategyDecisionAtUtc = frame.Market.AvailableAtUtc }]
                : [];

        public HistoricalReplayCandidate? AssessDeferredEntry(
            HistoricalReplayPortfolioFrame frame,
            HistoricalReplayCandidate pending,
            HistoricalReplayQuote optionQuote) => pending with
            {
                DecidedAtUtc = optionQuote.AvailableAtUtc,
                RequiresDeferredQuoteAdmission = false,
                PlannedStopPrice = 90m,
                PlannedTargetPrice = 115m
            };
    }

    private sealed class PlannedEntryPolicy : IHistoricalReplayPortfolioPolicy
    {
        public string CodeVersion => "planned-entry-test-v1";
        public IReadOnlyList<HistoricalReplayCandidate> Evaluate(
            HistoricalReplayPortfolioFrame frame) =>
            frame.Market.AvailableAtUtc == Start.AddMinutes(1)
                ? [new HistoricalReplayCandidate("NIFTY22000CE",
                    frame.Market.AvailableAtUtc, CodeVersion, .8m, 75)
                { PlannedStopPrice = 90m, PlannedTargetPrice = 125m }]
                : [];
    }

    private sealed class VoluntaryExitPolicy : IHistoricalReplayPortfolioPolicy
    {
        public string CodeVersion => "voluntary-exit-test-v1";
        public IReadOnlyList<HistoricalReplayCandidate> Evaluate(
            HistoricalReplayPortfolioFrame frame) =>
            frame.Market.AvailableAtUtc == Start.AddMinutes(1)
                ? [new HistoricalReplayCandidate("NIFTY22000CE",
                    frame.Market.AvailableAtUtc, CodeVersion, .8m, 75)] : [];
        public IReadOnlyList<HistoricalReplayExitRequest> EvaluateExits(
            HistoricalReplayPortfolioFrame frame) =>
            frame.Market.AvailableAtUtc == Start.AddMinutes(3)
                ? [new("NIFTY22000CE", frame.Market.AvailableAtUtc,
                    "ConfirmedReversal")] : [];
    }

    private sealed class TargetExtensionPolicy : IHistoricalReplayPortfolioPolicy
    {
        public string CodeVersion => "target-extension-test-v1";
        public IReadOnlyList<HistoricalReplayCandidate> Evaluate(
            HistoricalReplayPortfolioFrame frame) =>
            frame.Market.AvailableAtUtc == Start.AddMinutes(1)
                ? [new HistoricalReplayCandidate("NIFTY22000CE",
                    frame.Market.AvailableAtUtc, CodeVersion, .8m, 75)]
                : [];
        public bool ShouldExtendTarget(HistoricalReplayPortfolioFrame frame,
            HistoricalReplayOpenPosition position) => true;
    }

    private sealed class DeferredEntryPolicy : IHistoricalReplayPortfolioPolicy
    {
        public string CodeVersion => "deferred-entry-test-v1";
        public IReadOnlyList<HistoricalReplayCandidate> Evaluate(
            HistoricalReplayPortfolioFrame frame) =>
            frame.Market.AvailableAtUtc == Start.AddMinutes(1)
                ? [new HistoricalReplayCandidate("NIFTY22000CE",
                    frame.Market.AvailableAtUtc, CodeVersion, .8m, 75)
                {
                    StrategyDecisionAtUtc = frame.Market.AvailableAtUtc,
                    DeferredNiftyAdmission = new(
                        new TradingSystem.Application.Broker.GrowwQuote(0m,
                            null, null, null, null, null, null, null, null),
                        true, 1.5m, 500m, 100m, 100m,
                        new DateOnly(2026, 10, 29),
                        new DateOnly(2026, 11, 3), 10m, 1m,
                        .05m, 75, 1,
                        new ProfessionalTradeAssessmentInput("nifty",
                            ProfessionalTradeRegime.Range, .8m, 1m,
                            null, null, null, null, 0, null, null,
                            0m, false, 0, true),
                        new TradingSystem.Application.Risk.PaperPortfolioRiskInput(
                            0m, 0m, 75, 1, 100_000m, 1_000_000m,
                            0m, 0m, 0m, 100_000m, 0, 8,
                            false, true, true))
                }] : [];
        public HistoricalReplayCandidate? AssessDeferredEntry(
            HistoricalReplayPortfolioFrame frame,
            HistoricalReplayCandidate pending,
            HistoricalReplayQuote optionQuote) => pending with
            {
                DecidedAtUtc = optionQuote.AvailableAtUtc,
                DeferredNiftyAdmission = null,
                PlannedStopPrice = 90m,
                PlannedTargetPrice = 115m
            };
    }

    private sealed class TerminalEntryPolicy : IHistoricalReplayPortfolioPolicy
    {
        public string CodeVersion => "terminal-entry-test-v1";
        public IReadOnlyList<HistoricalReplayCandidate> Evaluate(
            HistoricalReplayPortfolioFrame frame) =>
            [new("NIFTY22000CE", frame.Market.AvailableAtUtc,
                CodeVersion, 0.8m, 75)];
    }

    private sealed class StopUpdatePolicy(decimal newStop, int updateAtMinute = 3)
        : IHistoricalReplayPortfolioPolicy
    {
        public string CodeVersion => "stop-update-test-v1";
        public decimal? InitialStopAtMinute4 { get; private set; }
        public decimal? CurrentStopAtMinute4 { get; private set; }
        public IReadOnlyList<HistoricalReplayCandidate> Evaluate(
            HistoricalReplayPortfolioFrame frame)
        {
            if (frame.Market.AvailableAtUtc == Start.AddMinutes(4))
            {
                InitialStopAtMinute4 = frame.OpenPosition?.InitialStopPrice;
                CurrentStopAtMinute4 = frame.OpenPosition?.StopPrice;
            }
            return frame.Market.AvailableAtUtc == Start.AddMinutes(1)
                ? [new("NIFTY22000CE", frame.Market.AvailableAtUtc,
                    CodeVersion, 0.8m, 75)] : [];
        }
        public IReadOnlyList<HistoricalReplayStopUpdate> EvaluateStopUpdates(
            HistoricalReplayPortfolioFrame frame) =>
            frame.Market.AvailableAtUtc == Start.AddMinutes(updateAtMinute)
                ? [new("NIFTY22000CE", frame.Market.AvailableAtUtc, newStop)] : [];
    }

    private sealed class ConcurrentPolicy(bool duplicateSymbol = false)
        : IHistoricalReplayPortfolioPolicy
    {
        public string CodeVersion => "concurrent-test-v1";
        public int OpenSymbolsAtSecondFrame { get; private set; }
        public IReadOnlyList<HistoricalReplayCandidate> Evaluate(
            HistoricalReplayPortfolioFrame frame)
        {
            if (frame.Market.AvailableAtUtc != Start.AddMinutes(1))
            {
                OpenSymbolsAtSecondFrame = frame.OpenPositions.Count;
                return [];
            }
            return [new("NIFTY22000CE", frame.Market.AvailableAtUtc,
                CodeVersion, 0.8m, 75),
                new(duplicateSymbol ? "NIFTY22000CE" : "NIFTY22000PE",
                    frame.Market.AvailableAtUtc, CodeVersion, 0.8m, 75)];
        }
    }

    private sealed class MixedExchangePolicy : IHistoricalReplayPortfolioPolicy
    {
        public string CodeVersion => "mixed-exchange-test-v1";
        public IReadOnlyList<HistoricalReplayCandidate> Evaluate(
            HistoricalReplayPortfolioFrame frame) =>
            frame.Market.AvailableAtUtc == Start.AddMinutes(1)
                ? [new("NIFTY22000CE", frame.Market.AvailableAtUtc,
                        CodeVersion, .8m, 75),
                    new("SENSEX73000PE", frame.Market.AvailableAtUtc,
                        CodeVersion, .8m, 75)]
                : [];
    }

    private sealed class ObserveAsOfPolicy : IHistoricalReplayPortfolioPolicy
    {
        public string CodeVersion => "observe-as-of-v1";
        public decimal? VisibleQuote { get; private set; }
        public decimal? VisibleChainOi { get; private set; }
        public IReadOnlyList<HistoricalReplayCandidate> Evaluate(
            HistoricalReplayPortfolioFrame frame)
        {
            VisibleQuote = frame.Market.LatestQuotes.GetValueOrDefault(
                "NIFTY22000CE")?.LastPrice;
            VisibleChainOi = frame.LatestOptionChain?.Strikes.Single()
                .CallOpenInterest;
            return [];
        }
    }

    private sealed class ObserveChainPolicy : IHistoricalReplayPortfolioPolicy
    {
        public string CodeVersion => "observe-chain-v1";
        public List<bool> SawChain { get; } = [];
        public IReadOnlyList<HistoricalReplayCandidate> Evaluate(
            HistoricalReplayPortfolioFrame frame)
        {
            SawChain.Add(frame.LatestOptionChain is not null);
            return [];
        }
    }

    private sealed class ObserveMarketChainsPolicy : IHistoricalReplayPortfolioPolicy
    {
        public string CodeVersion => "market-chains-test-v1";
        public decimal? NiftyOi { get; private set; }
        public decimal? SensexOi { get; private set; }
        public bool LegacyViewSuppressed { get; private set; }
        public IReadOnlyList<HistoricalReplayCandidate> Evaluate(
            HistoricalReplayPortfolioFrame frame)
        {
            NiftyOi = frame.LatestOptionChainsByUnderlying.GetValueOrDefault(
                "NIFTY")?.Strikes.Single().CallOpenInterest;
            SensexOi = frame.LatestOptionChainsByUnderlying.GetValueOrDefault(
                "SENSEX")?.Strikes.Single().CallOpenInterest;
            LegacyViewSuppressed = frame.LatestOptionChain is null;
            return [];
        }
    }

    private sealed class ObservePortfolioPolicy : IHistoricalReplayPortfolioPolicy
    {
        public string CodeVersion => "observe-portfolio-v1";
        public int Frames { get; private set; }
        public int ObservedClosedAtFinalFrame { get; private set; }
        public List<decimal?> ObservedOpenPrices { get; } = [];
        public List<int> ObservedClosedCounts { get; } = [];
        public IReadOnlyList<HistoricalReplayCandidate> Evaluate(
            HistoricalReplayPortfolioFrame frame)
        {
            Frames++;
            ObservedOpenPrices.Add(frame.OpenPosition?.LatestPrice);
            ObservedClosedCounts.Add(frame.ClosedTrades.Count);
            Assert.All(frame.Market.CompletedBars,
                bar => Assert.True(bar.AvailableAtUtc <= frame.Market.AvailableAtUtc));
            Assert.All(frame.Market.LatestQuotes.Values,
                quote => Assert.True(quote.AvailableAtUtc <= frame.Market.AvailableAtUtc));
            ObservedClosedAtFinalFrame = frame.ClosedTrades.Count;
            if (frame.OpenPosition is not null || frame.ClosedTrades.Count > 0 || Frames != 1)
                return [];
            return [new("NIFTY22000CE", frame.Market.AvailableAtUtc,
                CodeVersion, 0.8m, 75)];
        }
    }

    private sealed class LastPriceTriggerPolicy : IHistoricalReplayPortfolioPolicy
    {
        public string CodeVersion => "ltp-trigger-test-v1";
        public IReadOnlyList<HistoricalReplayCandidate> Evaluate(
            HistoricalReplayPortfolioFrame frame) =>
            frame.Market.AvailableAtUtc == Start.AddMinutes(1)
                ? [new HistoricalReplayCandidate("NIFTY22000CE",
                    frame.Market.AvailableAtUtc, CodeVersion, .8m, 75)
                { TriggerOnLastPrice = true }]
                : [];
    }

    private static HistoricalReplayBar Bar(int minute) =>
        new(Start.AddMinutes(minute), 60, 100m, 101m, 99m, 100m)
            { Symbol = "NIFTY" };

    private static HistoricalReplayQuote Quote(int minute, decimal price) =>
        new("NIFTY22000CE", Start.AddMinutes(minute), price);
}
