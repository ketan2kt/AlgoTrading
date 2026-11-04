namespace TradingSystem.Application.Diagnostics;

public sealed record ProtectionMonitorRow(Guid Id,string Market,string Symbol,string Lifecycle,
    int? PaperUnits,int? LiveUnits,decimal? DesiredStop,string? StopOrderId,int? ConfirmedUnits,
    decimal? ConfirmedStop,DateTimeOffset? ConfirmedAt,string Finding,bool NeedsAttention);
public sealed record ProtectionMonitorReport(DateTimeOffset ReadAt,int Attention,IReadOnlyList<ProtectionMonitorRow> Rows);
public interface ILiveProtectionMonitor
{
    Task<ProtectionMonitorReport> ReadAsync(CancellationToken token);
}
