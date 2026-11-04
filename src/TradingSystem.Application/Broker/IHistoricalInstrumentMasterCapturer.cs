namespace TradingSystem.Application.Broker;

// Research-only capture. It must not update trading Instrument metadata.
public interface IHistoricalInstrumentMasterCapturer
{
    Task CaptureAsync(CancellationToken cancellationToken);
}
