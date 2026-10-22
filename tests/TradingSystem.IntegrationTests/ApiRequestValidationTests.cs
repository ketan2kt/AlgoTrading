using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using TradingSystem.Application.Broker;
using TradingSystem.Api.Controllers;

namespace TradingSystem.IntegrationTests;

public sealed class ApiRequestValidationTests
{
    [Fact]
    public void ReadOnlyDiagnosticsRequiresAStrongConfiguredKey()
    {
        Assert.False(ReadOnlyDiagnosticsKeyValidator.IsConfigured(null));
        Assert.False(ReadOnlyDiagnosticsKeyValidator.IsConfigured("too-short"));
        Assert.True(ReadOnlyDiagnosticsKeyValidator.IsConfigured(new string('a', 32)));
    }

    [Fact]
    public void ReadOnlyDiagnosticsAcceptsOnlyTheExactKey()
    {
        var configured = "c4bb99738f6a4f93a3909548eed335e937104cb4b0f44828be053fcc5f3cd0a7";

        Assert.True(ReadOnlyDiagnosticsKeyValidator.IsAuthorized(configured, configured));
        Assert.False(ReadOnlyDiagnosticsKeyValidator.IsAuthorized(configured, configured + "x"));
        Assert.False(ReadOnlyDiagnosticsKeyValidator.IsAuthorized(configured, null));
    }

    [Fact]
    public void GrowwTokenRequestUsesPropertyValidationCompatibleWithAspNetCore()
    {
        var request = new StoreGrowwTokenRequest { AccessToken = "short" };
        var results = new List<ValidationResult>();

        var valid = Validator.TryValidateObject(request,
            new ValidationContext(request), results, validateAllProperties: true);

        Assert.False(valid);
        Assert.Contains(results, value => value.MemberNames.Contains(nameof(request.AccessToken)));
    }

    [Fact]
    public void KillSwitchRequestUsesPropertyValidationCompatibleWithAspNetCore()
    {
        var request = new SetKillSwitchRequest { Active = true, Reason = "" };
        var results = new List<ValidationResult>();

        var valid = Validator.TryValidateObject(request,
            new ValidationContext(request), results, validateAllProperties: true);

        Assert.False(valid);
        Assert.Contains(results, value => value.MemberNames.Contains(nameof(request.Reason)));
    }

    [Fact]
    public void DailyLossOverrideRequestRequiresAnAuditReason()
    {
        var request = new SetDailyLossOverrideRequest { Active = true, Reason = "" };
        var results = new List<ValidationResult>();

        var valid = Validator.TryValidateObject(request,
            new ValidationContext(request), results, validateAllProperties: true);

        Assert.False(valid);
        Assert.Contains(results, value => value.MemberNames.Contains(nameof(request.Reason)));
    }

    [Fact]
    public async Task TokenStorageSuccessIsNotHiddenByInstrumentSynchronizationFailure()
    {
        var controller = new GrowwTokenController(
            new SuccessfulTokenVault(),
            new FailingInstrumentSynchronizer(),
            NullLogger<GrowwTokenController>.Instance);

        var action = await controller.Store(
            new StoreGrowwTokenRequest { AccessToken = new string('x', 32) },
            CancellationToken.None);

        var response = Assert.IsType<StoreGrowwTokenResponse>(
            Assert.IsType<OkObjectResult>(action.Result).Value);
        Assert.True(response.Token.IsConfigured);
        Assert.Null(response.InstrumentSynchronization);
        Assert.NotNull(response.InstrumentSynchronizationError);
    }

    private sealed class SuccessfulTokenVault : IGrowwTokenVault
    {
        private static readonly GrowwTokenStatus Status =
            new(true, false, DateTimeOffset.UtcNow.AddHours(12), DateTimeOffset.UtcNow, "Test");

        public Task<GrowwTokenStatus> GetStatusAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Status);

        public Task<string?> GetValidTokenAsync(CancellationToken cancellationToken) =>
            Task.FromResult<string?>("token");

        public Task<GrowwTokenStatus> StoreAsync(
            string accessToken,
            string actor,
            CancellationToken cancellationToken) => Task.FromResult(Status);
    }

    private sealed class FailingInstrumentSynchronizer : IGrowwInstrumentSynchronizer
    {
        public Task<GrowwInstrumentSyncResult> SynchronizeAsync(CancellationToken cancellationToken) =>
            Task.FromException<GrowwInstrumentSyncResult>(new InvalidOperationException("Test failure"));
    }
}
