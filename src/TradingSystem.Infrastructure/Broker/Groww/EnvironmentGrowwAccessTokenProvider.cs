using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using TradingSystem.Application.Broker;

namespace TradingSystem.Infrastructure.Broker.Groww;

internal sealed class EnvironmentGrowwAccessTokenProvider(
    IOptions<GrowwOptions> options,
    IServiceScopeFactory scopeFactory) : IGrowwAccessTokenProvider
{
    public async ValueTask<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var scope = scopeFactory.CreateAsyncScope();
        var vault = scope.ServiceProvider.GetRequiredService<IGrowwTokenVault>();
        var protectedToken = await vault.GetValidTokenAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(protectedToken))
        {
            return protectedToken;
        }

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

        return token;
    }
}
