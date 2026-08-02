using System.Text.Json;
using TradingSystem.Application.Regime;
using TradingSystem.Domain.Trading;
using TradingSystem.Infrastructure.Persistence;

namespace TradingSystem.Infrastructure.MarketData;

internal sealed class EfMarketRegimePersistence(TradingDbContext dbContext) : IMarketRegimePersistence
{
    public async Task PersistAsync(Guid tradingSessionId, MarketRegimeResult result,
        CancellationToken cancellationToken)
    {
        var explanation = JsonSerializer.Serialize(new
        {
            supportingFactors = result.SupportingFactors,
            contradictingFactors = result.ContradictingFactors
        });
        dbContext.MarketRegimeSnapshots.Add(new MarketRegimeSnapshot(Guid.NewGuid(), tradingSessionId,
            result.Regime, result.DirectionalBias, result.Confidence, result.DataQuality,
            result.TradingPermitted, explanation, result.ObservedAtUtc));
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
