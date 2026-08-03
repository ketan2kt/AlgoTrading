using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using TradingSystem.Application.Broker;
using TradingSystem.Api.Controllers;

namespace TradingSystem.IntegrationTests;

public sealed class ApiRequestValidationTests
{
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
