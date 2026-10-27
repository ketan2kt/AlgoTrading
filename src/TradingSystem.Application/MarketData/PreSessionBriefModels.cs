namespace TradingSystem.Application.MarketData;

public sealed record PreSessionEvidence(
    string Name,
    string Status,
    string? Value,
    string? Source,
    DateTimeOffset? SourceAtUtc,
    string Note);

public sealed record PreSessionMarketBrief(
    string Market,
    DateOnly SessionDate,
    string Stage,
    DateTimeOffset CapturedAtUtc,
    string Bias,
    string Headline,
    string Judgement,
    string Confidence,
    string JudgementReason,
    decimal? PreviousOpen,
    decimal? PreviousClose,
    decimal? PreviousHigh,
    decimal? PreviousLow,
    DateTimeOffset? PreviousSessionAtUtc,
    decimal? IndicativePrice,
    DateTimeOffset? IndicativePriceAtUtc,
    decimal? CallOiStrike,
    decimal? PutOiStrike,
    DateOnly? OptionExpiry,
    DateTimeOffset? OptionOiAtUtc,
    IReadOnlyList<string> Scenarios,
    IReadOnlyList<PreSessionEvidence> Evidence,
    IReadOnlyList<string> Limitations);

public sealed record PreSessionBriefReport(
    DateOnly SessionDate,
    DateTimeOffset GeneratedAtUtc,
    IReadOnlyList<PreSessionMarketBrief> Briefs,
    IReadOnlyList<string> MissingStages,
    IReadOnlyList<PreSessionObservedOutcome> Outcomes,
    IReadOnlyList<PreSessionScorecard> Scorecards);

public sealed record PreSessionScorecard(
    string Market,
    int ReviewedSessions,
    int DirectionalGuesses,
    int Correct,
    int Incorrect,
    decimal? AccuracyPercent);

public sealed record PreSessionObservedOutcome(
    string Market,
    DateOnly SessionDate,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    int RecordedCandles,
    DateTimeOffset LastCandleAtUtc,
    bool? CrossedPriorHigh,
    bool? CrossedPriorLow,
    string ActualDirection,
    string MorningJudgement,
    string Assessment,
    string Coverage,
    DateTimeOffset AssessedAtUtc);

public interface IPreSessionBriefReader
{
    Task<PreSessionBriefReport> ReadAsync(DateOnly sessionDate, CancellationToken cancellationToken);
}
