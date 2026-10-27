using System.Security.Claims;
using DocumentInvite.Api.Domain;
using DocumentInvite.Api.Features.Audit;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace DocumentInvite.Api.Features.Auth;

/// <summary>
/// Sign-in wrapper: rejects accounts whose access has expired, and audits every sign-in
/// outcome (success, failure and lockout) with ids and enum values only.
/// </summary>
public sealed class AppSignInManager(
    UserManager<AppUser> userManager,
    IHttpContextAccessor contextAccessor,
    IUserClaimsPrincipalFactory<AppUser> claimsFactory,
    IOptions<IdentityOptions> optionsAccessor,
    ILogger<SignInManager<AppUser>> logger,
    IAuthenticationSchemeProvider schemes,
    IUserConfirmation<AppUser> confirmation,
    IAuditLog audit,
    TimeProvider clock)
    : SignInManager<AppUser>(userManager, contextAccessor, claimsFactory, optionsAccessor, logger, schemes, confirmation)
{
    private bool IsExpired(AppUser user) => user.AccessExpiresAt is { } expiry && expiry <= clock.GetUtcNow();

    public override async Task<SignInResult> PasswordSignInAsync(
        string userName, string password, bool isPersistent, bool lockoutOnFailure)
    {
        var user = await UserManager.FindByNameAsync(userName);
        if (user is null)
        {
            // Unknown account: there is no id to record, and the name is never logged.
            await audit.RecordAsync(
                "auth.signin_failed", new AuditTarget("user"),
                AuditDetails.Empty.WithEnum("reason", SignInFailure.InvalidCredentials));
            return SignInResult.Failed;
        }

        return await PasswordSignInAsync(user, password, isPersistent, lockoutOnFailure);
    }

    public override async Task<SignInResult> PasswordSignInAsync(
        AppUser user, string password, bool isPersistent, bool lockoutOnFailure)
    {
        var wasLockedOut = await UserManager.IsLockedOutAsync(user);
        var result = await base.PasswordSignInAsync(user, password, isPersistent, lockoutOnFailure);
        var target = new AuditTarget("user", user.Id);

        if (result.Succeeded)
        {
            await audit.RecordAsync("auth.signin_succeeded", target);
            return result;
        }

        var reason = result.IsLockedOut ? SignInFailure.LockedOut
            : IsExpired(user) ? SignInFailure.AccessExpired
            : result.IsNotAllowed ? SignInFailure.NotAllowed
            : SignInFailure.InvalidCredentials;

        await audit.RecordAsync("auth.signin_failed", target, AuditDetails.Empty.WithEnum("reason", reason));
        if (result.IsLockedOut && !wasLockedOut)
        {
            await audit.RecordAsync("auth.locked_out", target);
        }

        return result;
    }

    /// <summary>Runs before the password is checked, so an expired account never gets a token.</summary>
    protected override async Task<SignInResult?> PreSignInCheck(AppUser user)
    {
        if (IsExpired(user))
        {
            return SignInResult.NotAllowed;
        }

        return await base.PreSignInCheck(user);
    }

    /// <summary>Refresh tokens are validated here; an expired account cannot renew its tokens.</summary>
    public override async Task<AppUser?> ValidateSecurityStampAsync(ClaimsPrincipal? principal)
    {
        var user = await base.ValidateSecurityStampAsync(principal);
        return user is not null && IsExpired(user) ? null : user;
    }
}
