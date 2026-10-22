namespace TradingSystem.Application.Diagnostics;

public sealed record OperationalAutomationReport(
    string Version,
    string Market,
    DateOnly TradingDate,
    string Kind,
    string Status,
    string Headline,
    IReadOnlyDictionary<string, string> Metrics,
    IReadOnlyList<string> Findings,
    bool ActionRequired,
    DateTimeOffset GeneratedAtUtc);

public interface IOperationalAutomationReportReader
{
    Task<IReadOnlyList<OperationalAutomationReport>> GetLatestAsync(
        DateOnly? tradingDate,
        string market,
        CancellationToken cancellationToken);
}
