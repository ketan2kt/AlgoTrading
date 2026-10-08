using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TradingSystem.Application.Execution;
using TradingSystem.Application.Strategies;
using TradingSystem.Infrastructure.Persistence;

namespace TradingSystem.Infrastructure.Execution;

internal static class DirectionalThesisShadowAudit
{
    public static async Task<PersistentDirectionalThesisV1> RecordAsync(
        TradingDbContext db,
        string market,
        Guid underlyingInstrumentId,
        DateTimeOffset candleTimeUtc,
        IReadOnlyList<StrategyPriceBar> bars,
        decimal vwap,
        decimal openingRangeHigh,
        decimal openingRangeLow,
        CancellationToken cancellationToken)
    {
        var existing = await db.MarketStrategyAudits.AsNoTracking().FirstOrDefaultAsync(value =>
            value.Market == market &&
            value.UnderlyingInstrumentId == underlyingInstrumentId &&
            value.CandleTimeUtc == candleTimeUtc &&
            value.Outcome == PersistentDirectionalThesisAnalyzerV1.AuditOutcome,
            cancellationToken);
        if (existing is not null)
            return Read(existing.ReasonsJson) ?? PersistentDirectionalThesisAnalyzerV1.Analyze(
                bars, vwap, openingRangeHigh, openingRangeLow);

        var previousJson = await db.MarketStrategyAudits.AsNoTracking()
            .Where(value => value.Market == market &&
                            value.UnderlyingInstrumentId == underlyingInstrumentId &&
                            value.CandleTimeUtc < candleTimeUtc &&
                            value.Outcome == PersistentDirectionalThesisAnalyzerV1.AuditOutcome)
            .OrderByDescending(value => value.CandleTimeUtc)
            .Select(value => value.ReasonsJson)
            .FirstOrDefaultAsync(cancellationToken);
        var assessment = PersistentDirectionalThesisAnalyzerV1.Analyze(bars, vwap,
            openingRangeHigh, openingRangeLow, Read(previousJson));
        db.MarketStrategyAudits.Add(new(Guid.NewGuid(), market, underlyingInstrumentId,
            candleTimeUtc, PersistentDirectionalThesisAnalyzerV1.AuditOutcome,
            assessment.Confidence, JsonSerializer.Serialize(assessment)));
        await db.SaveChangesAsync(cancellationToken);
        return assessment;
    }

    private static PersistentDirectionalThesisV1? Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<PersistentDirectionalThesisV1>(json); }
        catch (JsonException) { return null; }
    }
}
