using System.Security.Cryptography;
using System.Text.Json;

namespace TradingSystem.Application.Execution;

public sealed record SessionPolicyReceipt(DateOnly Date, DateTimeOffset ObservedAt, string BuildId,
    HistoricalNormalPolicyConfiguration Settings,string Fingerprint)
{
    public static HistoricalReplayParameters ComparisonExecution => HistoricalReplayParameters.Default with {
        StopPercent=.1m,TargetPercent=.1m,MaximumHoldMinutes=375,MinimumMinutesBetweenEntries=0,
        MaximumConcurrentPositions=10,AllowModeledHistoricalCosts=true
    };
    public static SessionPolicyReceipt Create(DateOnly date,DateTimeOffset at,string build,HistoricalNormalPolicyConfiguration settings) =>
        new(date,at,build,settings,Hash(date,at,build,settings));
    public bool ValidFor(DateOnly date) => Date==date && Settings is not null && Guid.TryParseExact(BuildId,"N",out _) &&
        DateOnly.FromDateTime(ObservedAt.ToOffset(TimeSpan.FromHours(5.5)).DateTime)==date &&
        ObservedAt<=new DateTimeOffset(date.ToDateTime(new TimeOnly(9,15)),TimeSpan.FromHours(5.5)) &&
        Fingerprint==Hash(date,ObservedAt,BuildId,Settings);
    private static string Hash(DateOnly date,DateTimeOffset at,string build,HistoricalNormalPolicyConfiguration settings) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new {date,at,build,settings})));
}
