using TradingSystem.Domain.Common;

namespace TradingSystem.Domain.Trading;

public sealed class PreSessionBriefSnapshot : Entity, IAppendOnlyEntity
{
    public PreSessionBriefSnapshot(Guid id, string market, DateOnly sessionDate, string stage,
        DateTimeOffset capturedAtUtc, string payloadJson) : base(id)
    {
        if (string.IsNullOrWhiteSpace(market) || string.IsNullOrWhiteSpace(stage) ||
            string.IsNullOrWhiteSpace(payloadJson))
            throw new ArgumentException("Pre-session brief values are required.");
        Market = market;
        SessionDate = sessionDate;
        Stage = stage;
        CapturedAtUtc = capturedAtUtc.ToUniversalTime();
        PayloadJson = payloadJson;
    }

    public string Market { get; private init; }
    public DateOnly SessionDate { get; private init; }
    public string Stage { get; private init; }
    public DateTimeOffset CapturedAtUtc { get; private init; }
    public string PayloadJson { get; private init; }
}
