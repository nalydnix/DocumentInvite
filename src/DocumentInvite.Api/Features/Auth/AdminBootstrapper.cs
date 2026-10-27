using DocumentInvite.Api.Domain;
using DocumentInvite.Api.Features.Audit;
using DocumentInvite.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;

namespace DocumentInvite.Api.Features.Auth;

/// <summary>
/// Creates the first Admin account on startup when <c>Bootstrap:AdminEmail</c> and
/// <c>Bootstrap:AdminPassword</c> are configured and no Admin exists yet. It never changes an
/// existing account.
/// </summary>
public sealed class AdminBootstrapper(
    IServiceProvider services, IConfiguration configuration, ILogger<AdminBootstrapper> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var email = configuration["Bootstrap:AdminEmail"];
        var password = configuration["Bootstrap:AdminPassword"];
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            return;
        }

        try
        {
            await using var scope = services.CreateAsyncScope();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            if ((await users.GetUsersInRoleAsync(StaffRoles.Admin)).Count > 0)
            {
                return;
            }

            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var audit = scope.ServiceProvider.GetRequiredService<IAuditLog>();
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

            var admin = new AppUser
            {
                UserName = email,
                Email = email,
                EmailConfirmed = true,
                DisplayName = configuration["Bootstrap:AdminDisplayName"] is { Length: > 0 } name ? name : "Administrator",
            };

            var created = await users.CreateAsync(admin, password);
            if (!created.Succeeded)
            {
                logger.LogError(
                    "The bootstrap admin could not be created: {Errors}",
                    string.Join(", ", created.Errors.Select(e => e.Code)));
                return;
            }

            await users.AddToRoleAsync(admin, StaffRoles.Admin);
            await audit.RecordAsync(
                "user.created", new AuditTarget("user", admin.Id),
                AuditDetails.Empty.WithCount("roleCount", 1).WithEnum("roleAdmin", RoleChange.Added));
            await transaction.CommitAsync(cancellationToken);
            logger.LogInformation("Created the bootstrap admin account {UserId}", admin.Id);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The database may not be migrated yet; the API should still start.
            logger.LogError(ex, "The bootstrap admin check failed");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
