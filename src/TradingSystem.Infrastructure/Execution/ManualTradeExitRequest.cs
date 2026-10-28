using Microsoft.EntityFrameworkCore;
using TradingSystem.Domain;
using TradingSystem.Domain.Trading;
using TradingSystem.Infrastructure.Persistence;

namespace TradingSystem.Infrastructure.Execution;

// An append-only command: the paper engine remains the sole owner of position closure.
// The live mirror observes that same closure and reconciles the broker position.
public static class ManualTradeExitRequest
{
    public static string Outcome(Guid tradeId) => $"ManualExitRequested:{tradeId:N}";
    public static string Key(string market, Guid tradeId) => $"ManualExit:{market}:{tradeId:N}";

    public static Task<bool> ExistsAsync(TradingDbContext db, string market, Guid tradeId,
        CancellationToken cancellationToken) => db.ApplicationSettings.AsNoTracking().AnyAsync(
            value => value.Mode == TradingMode.Paper && value.Key == Key(market, tradeId), cancellationToken);
}
