namespace TradingSystem.Application.Diagnostics;

public sealed record ProtectionMonitorRow(Guid Id,string Market,string Symbol,string Lifecycle,
    int? PaperUnits,int? LiveUnits,decimal? DesiredStop,string? StopOrderId,int? ConfirmedUnits,
    decimal? ConfirmedStop,DateTimeOffset? ConfirmedAt,string Finding,bool NeedsAttention)
{
    public DateOnly SessionDate { get; init; }
    public string? ReviewVersion { get; init; }
    public DateTimeOffset? ReviewedAt { get; init; }
}
public sealed record ProtectionMonitorReport(DateTimeOffset ReadAt,int Attention,IReadOnlyList<ProtectionMonitorRow> Rows);
public interface ILiveProtectionMonitor
{
    Task<ProtectionMonitorReport> ReadAsync(CancellationToken token);
    Task<bool> ReviewAsync(Guid intentId,string version,string actor,CancellationToken token);
}
