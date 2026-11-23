namespace TradingSystem.Application.Broker;

public sealed record UpstoxExecutionReadiness(bool Configured, bool Authorized, bool StaticIpVerified,
    bool Sandbox, DateTimeOffset? ExpiresAtUtc, string[] Exchanges, string Message);

public interface IUpstoxExecutionAuthorization
{
    string AuthorizationUrl(string state);
    Task CompleteAsync(string code, string actor, CancellationToken token);
    Task<UpstoxExecutionReadiness> GetStatusAsync(CancellationToken token);
}
