using System.Text.Json;
using TradingSystem.Application.Execution;

namespace TradingSystem.Infrastructure.Execution;

// Keep option and underlying prices explicitly named. Case-only anonymous
// property names (price/Price) make System.Text.Json throw before the exit saves.
internal static class FailedSetupExitAudit
{
    internal static string Serialize(Guid positionId, decimal optionPrice,
        decimal underlyingPrice, FailedSetupExitDecision decision) =>
        JsonSerializer.Serialize(new
        {
            positionId,
            price = optionPrice,
            underlyingPrice,
            decision.AdverseR,
            decision.Reasons
        });
}
