namespace TradingSystem.Application.Execution;

public sealed record HistoricalReplayArchiveSlice(HistoricalReplaySessionData Session,
    decimal? PreviousClose, string? FrontFutureSymbol,
    IReadOnlyList<NiftyOptionContractCandidate> OptionContracts,
    IReadOnlyList<string> UnmappedGrowwSymbols)
{
    public Guid UnderlyingInstrumentId { get; init; }
    public IReadOnlyList<string> ControlStateGaps { get; init; } = [];
    public IReadOnlyList<HistoricalUniversePageEvidence> UniversePages { get; init; } = [];
    public IReadOnlyDictionary<string, string> ProviderSymbolToReplaySymbol { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
    public string? InstrumentMasterSnapshotFingerprint { get; init; }
    public DateTimeOffset? InstrumentMasterCapturedAtUtc { get; init; }
    public PersistentDirectionalThesisV1? PriorSensexThesis { get; init; }
}

public interface IHistoricalReplayArchiveReader
{
    Task<HistoricalReplayArchiveSlice> ReadAsync(DateOnly tradingDate, string market,
        CancellationToken cancellationToken);
}
