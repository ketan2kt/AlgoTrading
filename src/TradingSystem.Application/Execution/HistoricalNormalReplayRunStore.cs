namespace TradingSystem.Application.Execution;

public sealed record HistoricalNormalReplaySavedRun(DateTimeOffset CreatedAtUtc,
    string ExecutionBuildId, HistoricalNormalReplayComparisonRequest Request,
    IReadOnlyList<HistoricalReplayArchiveSlice> Inputs, HistoricalNormalReplayComparisonReport Report)
{
    public IReadOnlyDictionary<string, HistoricalQuotePathReference> QuotePaths { get; init; } = new Dictionary<string, HistoricalQuotePathReference>();
}

public sealed record HistoricalNormalReplayRunSummary(string Id, DateTimeOffset CreatedAtUtc,
    DateOnly From, DateOnly To, string? BaselineBuildId, string? ProposalBuildId,
    int IncludedDays, decimal BaselineNet, decimal ProposalNet)
{
    public string ViewFingerprint { get; init; } = string.Empty;
}

public sealed record HistoricalNormalReplaySavedView(HistoricalNormalReplayComparisonRequest Request,
    HistoricalNormalReplayComparisonReport Report);

public interface IHistoricalNormalReplayRunStore
{
    Task<string> SaveAsync(HistoricalNormalReplaySavedRun run, CancellationToken cancellationToken);
    Task<HistoricalNormalReplaySavedRun> LoadAsync(string id, CancellationToken cancellationToken);
    Task<HistoricalNormalReplaySavedView> ViewAsync(string id, CancellationToken cancellationToken);
    Task<IReadOnlyList<HistoricalNormalReplayRunSummary>> ListAsync(CancellationToken cancellationToken);
}
