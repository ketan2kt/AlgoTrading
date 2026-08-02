using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingSystem.Domain.Trading;
using TradingSystem.Infrastructure.Persistence;

namespace TradingSystem.Infrastructure.Identity;

public sealed partial class IdentityBootstrapService(
    IOptions<IdentityBootstrapOptions> options,
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<IdentityBootstrapService> logger) : IHostedService
{
    private const string AdministratorRole = "Administrator";

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.Enabled)
        {
            return;
        }

        Validate(options.Value);
        await using var scope = scopeFactory.CreateAsyncScope();
        var userManager = scope.ServiceProvider
            .GetRequiredService<UserManager<ApplicationUser>>();
        var roleManager = scope.ServiceProvider
            .GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        var dbContext = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        if (!await roleManager.RoleExistsAsync(AdministratorRole))
        {
            var roleResult = await roleManager.CreateAsync(
                new IdentityRole<Guid>(AdministratorRole));
            EnsureSucceeded(roleResult, "create the Administrator role");
        }

        var now = timeProvider.GetUtcNow();
        var user = await userManager.FindByNameAsync(options.Value.Username);
        var action = "AdministratorPasswordReset";
        if (user is null)
        {
            user = new ApplicationUser
            {
                Id = Guid.NewGuid(),
                UserName = options.Value.Username,
                Email = options.Value.Email,
                EmailConfirmed = true,
                CreatedAtUtc = now,
                IsActive = true
            };
            var createResult = await userManager.CreateAsync(user, options.Value.Password);
            EnsureSucceeded(createResult, "create the bootstrap administrator");
            action = "AdministratorCreated";
        }
        else
        {
            if (!string.Equals(user.Email, options.Value.Email, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "The existing administrator username belongs to a different email address.");
            }

            var resetToken = await userManager.GeneratePasswordResetTokenAsync(user);
            var resetResult = await userManager.ResetPasswordAsync(
                user,
                resetToken,
                options.Value.Password);
            EnsureSucceeded(resetResult, "reset the bootstrap administrator password");
        }

        if (!await userManager.IsInRoleAsync(user, AdministratorRole))
        {
            var roleAssignment = await userManager.AddToRoleAsync(user, AdministratorRole);
            EnsureSucceeded(roleAssignment, "assign the Administrator role");
        }

        dbContext.AuditLogs.Add(new AuditLog(
            Guid.NewGuid(),
            "system/bootstrap",
            action,
            nameof(ApplicationUser),
            user.Id.ToString(),
            "One-time identity bootstrap",
            "{}",
            """{"role":"Administrator"}""",
            Guid.NewGuid().ToString(),
            now));
        await dbContext.SaveChangesAsync(cancellationToken);

        LogBootstrapCompleted(logger, user.Id, action);
        throw new InvalidOperationException(
            "Administrator provisioned. Disable IdentityBootstrap and remove its password secret before restarting.");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static void Validate(IdentityBootstrapOptions value)
    {
        if (string.IsNullOrWhiteSpace(value.Username) ||
            string.IsNullOrWhiteSpace(value.Email) ||
            string.IsNullOrWhiteSpace(value.Password))
        {
            throw new InvalidOperationException(
                "Enabled identity bootstrap requires username, email, and password.");
        }
    }

    private static void EnsureSucceeded(IdentityResult result, string operation)
    {
        if (!result.Succeeded)
        {
            var codes = string.Join(",", result.Errors.Select(error => error.Code));
            throw new InvalidOperationException($"Unable to {operation}. Codes: {codes}");
        }
    }

    [LoggerMessage(
        EventId = 2000,
        Level = LogLevel.Warning,
        Message = "Bootstrap administrator {UserId} provisioned via {Action}; bootstrap must now be disabled.")]
    private static partial void LogBootstrapCompleted(
        ILogger logger,
        Guid userId,
        string action);
}
