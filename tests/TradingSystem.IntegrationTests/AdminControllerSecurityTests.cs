using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TradingSystem.Api.Controllers;

namespace TradingSystem.IntegrationTests;

public sealed class AdminControllerSecurityTests
{
    [Fact]
    public void AdminApiRequiresAdministratorRole()
    {
        var authorization = Assert.Single(typeof(AdminController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>());

        Assert.Equal("Administrator", authorization.Roles);
    }

    [Fact]
    public void CreatingUsersRequiresAntiForgeryProtection()
    {
        var endpoint = typeof(AdminController).GetMethod(nameof(AdminController.CreatePendingUser));
        Assert.NotNull(endpoint);
        Assert.NotNull(endpoint.GetCustomAttributes(typeof(ValidateAntiForgeryTokenAttribute), inherit: true).SingleOrDefault());
    }
}
