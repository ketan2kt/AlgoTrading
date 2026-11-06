using Azure;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using System.Xml;
using System.Xml.Linq;
using TradingSystem.Infrastructure.Broker.Groww;
using TradingSystem.Infrastructure.Execution;
using TradingSystem.Infrastructure.MarketData;
using TradingSystem.Application.Broker;

namespace TradingSystem.Infrastructure;

public static class ReplayRecorderHosting
{
    public static bool IsRecorder(IConfiguration configuration) => configuration["ApplicationRole"] == "ReplayRecorder";

    public static void RestrictToRecorder(IServiceCollection services)
    {
        // Explicit allowlist: never inherit a newly added trading/order worker.
        services.RemoveAll<IHostedService>();
        services.AddHostedService(provider => provider.GetRequiredService<ReplayBlobStorage>());
        services.AddHostedService(provider => provider.GetRequiredService<HistoricalQuoteArchive>());
        services.AddHostedService<GrowwHistoricalFeedCaptureService>();
        services.AddHostedService<HistoricalAfterMarketBackfillService>();
        services.AddHostedService<HistoricalPolicyBuildCaptureService>();
        // Website owns master updates and daily token refresh. Recorder only
        // reads the encrypted vault; it cannot generate competing broker tokens.
        services.RemoveAll<IGrowwAccessTokenProvider>();
        services.AddSingleton<IGrowwAccessTokenProvider, RecorderAccessTokenProvider>();
    }

    public static async Task ConfigureSharedKeysAsync(IDataProtectionBuilder protection,
        IConfiguration configuration, DirectoryInfo existingKeys, CancellationToken token = default)
    {
        // Website retains its existing local key repository: a cloud outage
        // must not prevent trading-host startup or sign-in. Its background
        // publisher mirrors the key ring for the read-only recorder.
        if (!IsRecorder(configuration)) return;
        var address = configuration["ReplayStorage:KeyRingUri"];
        if (string.IsNullOrWhiteSpace(address))
        {
            if (IsRecorder(configuration)) throw new InvalidOperationException("Recorder requires the shared protected-token key ring.");
            return;
        }
        var uri = new Uri(address);
        ReplayBlobStorage.ValidateContainerUri(new Uri(uri, ".").AbsoluteUri.TrimEnd('/'));
        if (uri.Query.Length != 0 || uri.Fragment.Length != 0 || !uri.AbsolutePath.EndsWith("/keyring.xml", StringComparison.Ordinal))
            throw new InvalidOperationException("Invalid shared key ring address.");
        var blob = new BlobClient(uri, new ManagedIdentityCredential(ManagedIdentityId.SystemAssigned));
        if (!(await blob.ExistsAsync(token)).Value)
        {
            throw new InvalidOperationException("Website must initialize the shared key ring first.");
        }
        protection.PersistKeysToAzureBlobStorage(blob);
        if (IsRecorder(configuration)) protection.DisableAutomaticKeyGeneration();
    }

    private sealed class RecorderAccessTokenProvider(IServiceScopeFactory scopes) : IGrowwAccessTokenProvider
    {
        public async ValueTask<string> GetAccessTokenAsync(CancellationToken cancellationToken)
        {
            await using var scope = scopes.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<IGrowwTokenVault>().GetValidTokenAsync(cancellationToken)
                ?? throw new InvalidOperationException("Awaiting website broker-token refresh.");
        }
    }
}
