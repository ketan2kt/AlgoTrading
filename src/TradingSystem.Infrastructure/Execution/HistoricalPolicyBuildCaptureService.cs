using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TradingSystem.Application.Execution;

namespace TradingSystem.Infrastructure.Execution;

// Capture each deployed normal-policy build before it can be replaced. This is
// a local artifact copy only; research endpoints can retry a failed capture.
internal sealed class HistoricalPolicyBuildCaptureService(IHistoricalPolicyBuildStore builds,
    ILogger<HistoricalPolicyBuildCaptureService> logger) : BackgroundService
{
    private static readonly Action<ILogger, Exception?> CaptureFailed = LoggerMessage.Define(
        LogLevel.Error, new EventId(6751, "HistoricalPolicyBuildCaptureFailed"),
        "Historical policy build archival failed; research must not substitute a different build.");
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await builds.CaptureCurrentAsync(stoppingToken); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            CaptureFailed(logger, exception);
        }
    }
}
