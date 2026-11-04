using System.Text.Json;

namespace TradingSystem.Application.Execution;

public static class HistoricalExperimentReview
{
    public static (string Finding, bool VerifiedControlWithFills) Assess(HistoricalNormalReplayComparisonRequest request,
        HistoricalSharedPortfolioResult result, bool sameBuild)
    {
        if (result.Days.Count == 0) return ("No eligible sessions. No conclusion.", false);
        var baseline = JsonSerializer.Serialize(request.Baseline);
        if (sameBuild && baseline == JsonSerializer.Serialize(request.Proposal))
        {
            foreach (var day in result.Days)
            {
                var a = day.Baseline;
                var b = day.Proposal;
                if (!a.Trades.SequenceEqual(b.Trades) || a.NetPnl != b.NetPnl ||
                    a.MaximumDrawdown != b.MaximumDrawdown || a.CandidateCount != b.CandidateCount ||
                    a.MissingEntryQuoteCount != b.MissingEntryQuoteCount || a.MissingExitQuoteCount != b.MissingExitQuoteCount ||
                    a.MissingAddOnQuoteCount != b.MissingAddOnQuoteCount || a.StaleQuoteCount != b.StaleQuoteCount ||
                    a.UnsupportedPartialCount != b.UnsupportedPartialCount || a.PeakConcurrentPositions != b.PeakConcurrentPositions ||
                    a.PeakEntryCapitalExposure != b.PeakEntryCapitalExposure)
                    return ($"A/A mismatch on {day.Date:yyyy-MM-dd}. Do not interpret proposal results until investigated.", false);
            }
            return result.BaselineTrades == 0 ? ("A/A matched without completed trades. Fill behavior remains untested.", false) :
                ("A/A matched: every modeled fill, session result and execution counter agreed. This checks reproducibility, not live fills or profitability.", true);
        }
        var fixedTarget = request.Baseline with {
            NiftyExits = request.Baseline.NiftyExits with { TargetExtensionEnabled = false },
            MarketExits = request.Baseline.MarketExits with { TargetExtensionEnabled = false }
        };
        if (!sameBuild || JsonSerializer.Serialize(fixedTarget) != JsonSerializer.Serialize(request.Proposal))
            return ("Strategy comparison; inspect the held-out dates and evidence limitations.", false);
        if (result.EvidenceGaps.Count != 0 || result.ValidationDays == 0)
            return ("Target extension off: exploratory only. Evidence gaps prevent a recommendation.", false);
        return (result.ValidationNetDifference > 0 && result.ValidationProposal.WorstSessionDrawdown <= result.ValidationBaseline.WorstSessionDrawdown
            ? "Fixed targets improved held-out net without a worse held-out drawdown. Candidate for further forward testing."
            : "Fixed targets did not improve held-out net without worsening drawdown. Keep the current policy.", false);
    }
}
