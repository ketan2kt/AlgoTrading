using TradingSystem.Domain.Common;

namespace TradingSystem.Domain.Trading;

// Immutable observed broker-master evidence. CapturedAtUtc is when our system
// received it, not a claim that these contracts existed at an earlier time.
public sealed class HistoricalInstrumentMasterSnapshot : Entity, IAppendOnlyEntity
{
    public HistoricalInstrumentMasterSnapshot(Guid id, DateOnly sessionDate,
        DateTimeOffset capturedAtUtc, string source, string fingerprint,
        int contractCount, string payloadJson) : base(id)
    {
        if (sessionDate == default || capturedAtUtc == default ||
            string.IsNullOrWhiteSpace(source) ||
            string.IsNullOrWhiteSpace(fingerprint) || contractCount < 1 ||
            string.IsNullOrWhiteSpace(payloadJson))
            throw new ArgumentException("A dated instrument-master snapshot is required.");
        SessionDate = sessionDate;
        CapturedAtUtc = capturedAtUtc.ToUniversalTime();
        Source = source;
        Fingerprint = fingerprint;
        ContractCount = contractCount;
        PayloadJson = payloadJson;
    }

    public DateOnly SessionDate { get; private init; }
    public DateTimeOffset CapturedAtUtc { get; private init; }
    public string Source { get; private init; }
    public string Fingerprint { get; private init; }
    public int ContractCount { get; private init; }
    public string PayloadJson { get; private init; }
}
