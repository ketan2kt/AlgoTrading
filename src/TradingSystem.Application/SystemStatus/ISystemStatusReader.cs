namespace TradingSystem.Application.SystemStatus;

public interface ISystemStatusReader
{
    SystemStatusSnapshot GetCurrent();
}

