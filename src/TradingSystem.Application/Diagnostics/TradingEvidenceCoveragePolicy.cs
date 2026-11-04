namespace TradingSystem.Application.Diagnostics;

public static class TradingEvidenceCoveragePolicy
{
    public static string? Warning(string market, DateOnly date,
        DateTimeOffset? latestRegularDecisionAtUtc, DateTimeOffset? latestEngineEvidenceAtUtc,
        DateTimeOffset? lastCandleAtUtc)
    {
        if (lastCandleAtUtc is null) return null;
        var latest = latestRegularDecisionAtUtc is null ? latestEngineEvidenceAtUtc :
            latestEngineEvidenceAtUtc is null ? latestRegularDecisionAtUtc :
            latestRegularDecisionAtUtc > latestEngineEvidenceAtUtc
                ? latestRegularDecisionAtUtc : latestEngineEvidenceAtUtc;
        if (latest is null)
            return $"Missing normal-engine evidence for {market.ToUpperInvariant()}; " +
                "specialty activity does not prove regular evaluation ran. No exact blocker can be inferred.";
        var cutoff = new DateTimeOffset(date.ToDateTime(new TimeOnly(15, 0)), TimeSpan.FromHours(5.5));
        var expectedThrough = lastCandleAtUtc > cutoff ? cutoff : lastCandleAtUtc.Value;
        return expectedThrough - latest.Value > TimeSpan.FromMinutes(5)
            ? $"Regular-engine evidence gap for {market.ToUpperInvariant()}: latest decision or " +
              $"prerequisite evidence is {latest:O}, but candles extend through {expectedThrough:O}."
            : null;
    }
}
