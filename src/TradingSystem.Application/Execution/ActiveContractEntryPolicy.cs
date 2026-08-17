using TradingSystem.Domain.Trading;

namespace TradingSystem.Application.Execution;

public sealed record ActiveContractExposure(Guid InstrumentId, Direction Direction);

public static class ActiveContractEntryPolicy
{
    public static bool IsDuplicate(
        IEnumerable<ActiveContractExposure> activeExposures,
        Guid proposedInstrumentId,
        Direction proposedDirection)
    {
        ArgumentNullException.ThrowIfNull(activeExposures);
        if (proposedInstrumentId == Guid.Empty)
            throw new ArgumentException("A proposed instrument is required.", nameof(proposedInstrumentId));

        return activeExposures.Any(value =>
            value.InstrumentId == proposedInstrumentId && value.Direction == proposedDirection);
    }
}
