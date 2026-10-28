using DocumentInvite.Api.Common;
using DocumentInvite.Api.Common.Paging;
using DocumentInvite.Api.Domain;
using DocumentInvite.Api.Features.Audit;
using DocumentInvite.Api.Features.Auth;
using DocumentInvite.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace DocumentInvite.Api.Features.Users;

/// <summary>The outcome of an admin operation: a value, "not found", or field-level errors.</summary>
public sealed record UserResult(UserResponse? User, IReadOnlyDictionary<string, string[]>? Errors, bool NotFound)
{
    public static UserResult Ok(UserResponse user) => new(user, null, false);
    public static UserResult Missing() => new(null, null, true);
    public static UserResult Invalid(string field, params string[] messages) =>
        new(null, new Dictionary<string, string[]> { [field] = messages }, false);
}

public sealed class UserAdminService(
    UserManager<AppUser> users, AppDbContext db, IAuditLog audit, IRequestContext context, TimeProvider clock)
{
    public async Task<PagedResponse<UserResponse>> ListAsync(PageQuery query, CancellationToken ct)
    {
        var page = await users.Users.AsNoTracking()
            .OrderBy(x => x.Email).ThenBy(x => x.Id)
            .ToPagedResponseAsync(query, u => u, ct);

        // Roles are read only for the users on this page.
        var ids = page.Items.Select(u => u.Id).ToList();
        var rolesByUser = await (
                from link in db.UserRoles
                join role in db.Roles on link.RoleId equals role.Id
                where ids.Contains(link.UserId)
                select new { link.UserId, role.Name })
            .ToListAsync(ct);

        var items = page.Items
            .Select(u => ToResponse(u, HighestRole(rolesByUser.Where(r => r.UserId == u.Id).Select(r => r.Name!))))
            .ToList();
        return new PagedResponse<UserResponse>(items, page.Total, page.Page, page.PageSize);
    }

    public async Task<UserResult> GetAsync(Guid id)
    {
        var user = await users.FindByIdAsync(id.ToString());
        return user is null ? UserResult.Missing() : UserResult.Ok(await ToResponseAsync(user));
    }

    public async Task<UserResult> CreateAsync(CreateUserRequest request, CancellationToken ct)
    {
        var role = request.Role!;
        var user = new AppUser
        {
            UserName = request.Email,
            Email = request.Email,
            EmailConfirmed = true,
            DisplayName = request.DisplayName!.Trim(),
            AccessExpiresAt = request.AccessExpiresAt,
        };

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var created = await users.CreateAsync(user, request.Password!);
        if (!created.Succeeded)
        {
            return FromIdentityErrors(created);
        }

        await users.AddToRoleAsync(user, role);

        var details = AuditDetails.Empty.WithCount("roleCount", 1).WithEnum("role" + role, RoleChange.Added);
        if (user.AccessExpiresAt is not null)
        {
            details.WithEnum("expiry", ExpiryChange.Set);
        }

        await audit.RecordAsync("user.created", new AuditTarget("user", user.Id), details, ct);
        await transaction.CommitAsync(ct);

        return UserResult.Ok(ToResponse(user, role));
    }

    public async Task<UserResult> SetRoleAsync(Guid id, SetRoleRequest request, CancellationToken ct)
    {
        var user = await users.FindByIdAsync(id.ToString());
        if (user is null)
        {
            return UserResult.Missing();
        }

        var wanted = request.Role!;
        var current = (await users.GetRolesAsync(user)).ToList();
        var added = current.Contains(wanted) ? new List<string>() : [wanted];
        var removed = current.Where(r => r != wanted).ToList();

        if (removed.Contains(StaffRoles.Admin) && context.ActorId == user.Id)
        {
            return UserResult.Invalid("role", "You cannot remove the Admin role from your own account.");
        }

        if (added.Count == 0 && removed.Count == 0)
        {
            return UserResult.Ok(ToResponse(user, wanted));
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        await users.RemoveFromRolesAsync(user, removed);
        await users.AddToRolesAsync(user, added);
        // Invalidates refresh tokens, so the new roles apply at the next sign-in.
        await users.UpdateSecurityStampAsync(user);

        var details = RoleDetails(AuditDetails.Empty.WithCount("roleCount", 1), added, RoleChange.Added);
        RoleDetails(details, removed, RoleChange.Removed);
        await audit.RecordAsync("user.roles_changed", new AuditTarget("user", user.Id), details, ct);

        // An access expiry only makes sense for an Auditor.
        if (wanted != StaffRoles.Auditor && user.AccessExpiresAt is not null)
        {
            user.AccessExpiresAt = null;
            await users.UpdateAsync(user);
            await audit.RecordAsync(
                "user.access_expiry_changed", new AuditTarget("user", user.Id),
                AuditDetails.Empty.WithEnum("change", ExpiryChange.Cleared), ct);
        }

        await transaction.CommitAsync(ct);
        return UserResult.Ok(ToResponse(user, wanted));
    }

    public async Task<UserResult> SetAccessExpiryAsync(Guid id, SetAccessExpiryRequest request, CancellationToken ct)
    {
        var user = await users.FindByIdAsync(id.ToString());
        if (user is null)
        {
            return UserResult.Missing();
        }

        if (request.AccessExpiresAt is not null && !await users.IsInRoleAsync(user, StaffRoles.Auditor))
        {
            return UserResult.Invalid("accessExpiresAt", "An access expiry date can only be set for an Auditor.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        user.AccessExpiresAt = request.AccessExpiresAt;
        await users.UpdateAsync(user);
        await audit.RecordAsync(
            "user.access_expiry_changed", new AuditTarget("user", user.Id),
            AuditDetails.Empty.WithEnum("change", request.AccessExpiresAt is null ? ExpiryChange.Cleared : ExpiryChange.Set),
            ct);
        await transaction.CommitAsync(ct);

        return UserResult.Ok(await ToResponseAsync(user));
    }

    private static AuditDetails RoleDetails(AuditDetails details, IEnumerable<string> roles, RoleChange change)
    {
        foreach (var role in roles)
        {
            details.WithEnum("role" + role, change);
        }

        return details;
    }

    private static UserResult FromIdentityErrors(IdentityResult result)
    {
        var errors = result.Errors
            .GroupBy(e => e.Code.StartsWith("Password", StringComparison.Ordinal) ? "password" : "email")
            .ToDictionary(g => g.Key, g => g.Select(e => e.Description).ToArray());
        return new UserResult(null, errors, false);
    }

    private async Task<UserResponse> ToResponseAsync(AppUser user) =>
        ToResponse(user, HighestRole(await users.GetRolesAsync(user)));

    /// <summary>The most privileged of the given roles (Admin, LoanOfficer, Auditor), if any.</summary>
    private static string HighestRole(IEnumerable<string> roles) =>
        StaffRoles.All.FirstOrDefault(roles.Contains) ?? string.Empty;

    private UserResponse ToResponse(AppUser user, string role) =>
        new(user.Id, user.Email ?? string.Empty, user.DisplayName, role, user.AccessExpiresAt,
            user.LockoutEnd is { } end && end > clock.GetUtcNow());
}
