namespace TradingSystem.Application.Risk;

public sealed record AdaptivePaperTradeDecision(bool Permitted, IReadOnlyList<string> Reasons);

public static class AdaptivePaperTradePolicy
{
    public static AdaptivePaperTradeDecision Evaluate(int entriesToday, int maximumEntries,
        int consecutiveLosses, int maximumConsecutiveLosses, DateTimeOffset? lastLossAtUtc,
        DateTimeOffset nowUtc, int lossCooldownMinutes,
        bool renewedDirectionalOpportunity = false,
        int maximumDirectionalRecoveryEntries = 0)
    {
        var reasons = new List<string>();
        var effectiveMaximum = maximumEntries +
                               (renewedDirectionalOpportunity ? maximumDirectionalRecoveryEntries : 0);
        if (entriesToday >= effectiveMaximum)
            reasons.Add(renewedDirectionalOpportunity
                ? $"Directional recovery allocation of {effectiveMaximum} total entries is exhausted."
                : $"Evidence-based daily research allocation of {maximumEntries} entries is exhausted.");
        if (consecutiveLosses >= maximumConsecutiveLosses && !renewedDirectionalOpportunity)
            reasons.Add($"Trading is paused after {consecutiveLosses} consecutive net losses.");
        if (lastLossAtUtc is not null && nowUtc - lastLossAtUtc < TimeSpan.FromMinutes(lossCooldownMinutes))
        {
            var remaining = TimeSpan.FromMinutes(lossCooldownMinutes) - (nowUtc - lastLossAtUtc.Value);
            reasons.Add($"Post-loss cooldown is active for another {Math.Ceiling(remaining.TotalMinutes)} minute(s).");
        }
        return new(reasons.Count == 0, reasons);
    }
}
