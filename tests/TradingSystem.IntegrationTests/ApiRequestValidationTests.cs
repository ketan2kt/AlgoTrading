using System.ComponentModel.DataAnnotations;
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
}
