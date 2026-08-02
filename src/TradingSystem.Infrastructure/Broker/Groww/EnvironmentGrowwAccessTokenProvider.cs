using Microsoft.Extensions.Options;

namespace TradingSystem.Infrastructure.Broker.Groww;

internal sealed class EnvironmentGrowwAccessTokenProvider(
    IOptions<GrowwOptions> options) : IGrowwAccessTokenProvider
{
    public ValueTask<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var variableName = options.Value.AccessTokenEnvironmentVariable;
        if (string.IsNullOrWhiteSpace(variableName))
        {
            throw new InvalidOperationException(
                "Groww access-token environment variable name is not configured.");
        }

        var token = Environment.GetEnvironmentVariable(variableName);
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException(
                $"Groww access token is unavailable in environment variable '{variableName}'.");
        }

        return ValueTask.FromResult(token);
    }
}
