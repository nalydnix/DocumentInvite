using System.Security.Claims;
using System.Security.Cryptography;
using DocumentInvite.Api.Common;
using DocumentInvite.Api.Domain;
using DocumentInvite.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace DocumentInvite.Api.Features.Portal;

public sealed class PortalSessionOptions
{
    public const string Section = "Portal:Session";

    /// <summary>
    /// Base64 key (at least 32 bytes) that signs portal session tokens, from configuration only
    /// (<c>Portal__Session__SigningKey</c>). When empty, a random key is created at startup, so sessions
    /// end when the process restarts; set it when running more than one instance.
    /// </summary>
    public string? SigningKey { get; set; }

    /// <summary>How long a portal session lasts.</summary>
    public int LifetimeMinutes { get; set; } = 60;

    /// <summary>Session exchange attempts allowed per client address per minute.</summary>
    public int RateLimitPerMinute { get; set; } = 10;
}

public static class PortalAuth
{
    public const string Scheme = "PortalSession";
    public const string Policy = "PortalSession";
    public const string RateLimitPolicy = "portal-session";
    public const string InviteClaim = "inviteId";
    public const string TokenClaim = "tokenId";
    public const string Issuer = "documentinvite-portal";
}

/// <summary>Holds the signing key for portal session tokens.</summary>
public sealed class PortalSessionKey
{
    public PortalSessionKey(IOptions<PortalSessionOptions> options)
    {
        var configured = options.Value.SigningKey;
        var bytes = string.IsNullOrWhiteSpace(configured) ? RandomNumberGenerator.GetBytes(32) : Convert.FromBase64String(configured);
        if (bytes.Length < 32)
        {
            throw new InvalidOperationException("Portal:Session:SigningKey must be at least 32 bytes (base64).");
        }

        Key = new SymmetricSecurityKey(bytes) { KeyId = "portal-session" };
    }

    public SymmetricSecurityKey Key { get; }
}

public sealed record PortalSession(string Token, DateTimeOffset ExpiresAt);

/// <summary>Issues short-lived session tokens scoped to one invite.</summary>
public sealed class PortalSessionIssuer(PortalSessionKey key, IOptions<PortalSessionOptions> options, TimeProvider clock)
{
    public PortalSession Issue(Invite invite)
    {
        var now = clock.GetUtcNow();
        var expires = now.AddMinutes(Math.Max(1, options.Value.LifetimeMinutes));
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = PortalAuth.Issuer,
            Audience = PortalAuth.Issuer,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            SigningCredentials = new SigningCredentials(key.Key, SecurityAlgorithms.HmacSha256),
            Subject = new ClaimsIdentity(
            [
                new Claim(PortalAuth.InviteClaim, invite.Id.ToString("D")),
                new Claim(HttpRequestContext.PartyContactClaim, invite.ContactId.ToString("D")),
                new Claim(PortalAuth.TokenClaim, TokenId(invite.TokenHash)),
            ]),
        };
        return new PortalSession(new JsonWebTokenHandler().CreateToken(descriptor), expires);
    }

    /// <summary>Ties a session to one portal link, so reissuing the link ends sessions opened with the old one.</summary>
    public static string TokenId(byte[] tokenHash) => Convert.ToBase64String(tokenHash, 0, 16);
}

public static class PortalSessionBinding
{
    /// <summary>Configures JWT validation for the portal scheme: signature, lifetime and the invite's current state.</summary>
    public static void Configure(JwtBearerOptions options, PortalSessionKey key)
    {
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = PortalAuth.Issuer,
            ValidAudience = PortalAuth.Issuer,
            IssuerSigningKey = key.Key,
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = PortalAuth.InviteClaim,
        };
        options.Events = new JwtBearerEvents
        {
            // A session ends as soon as its invite is revoked, expires, or its link is reissued.
            OnTokenValidated = async context =>
            {
                var services = context.HttpContext.RequestServices;
                var db = services.GetRequiredService<AppDbContext>();
                var clock = services.GetRequiredService<TimeProvider>();

                if (!Guid.TryParse(context.Principal?.FindFirstValue(PortalAuth.InviteClaim), out var inviteId))
                {
                    context.Fail("The session has no invite.");
                    return;
                }

                var invite = await db.Invites.AsNoTracking()
                    .Where(i => i.Id == inviteId)
                    .Select(i => new { i.TokenHash, i.RevokedAt, i.TokenExpiresAt })
                    .SingleOrDefaultAsync(context.HttpContext.RequestAborted);

                var tokenId = context.Principal?.FindFirstValue(PortalAuth.TokenClaim) ?? string.Empty;
                if (invite is null
                    || invite.RevokedAt is not null
                    || invite.TokenExpiresAt <= clock.GetUtcNow()
                    || !CryptographicOperations.FixedTimeEquals(
                        System.Text.Encoding.UTF8.GetBytes(PortalSessionIssuer.TokenId(invite.TokenHash)),
                        System.Text.Encoding.UTF8.GetBytes(tokenId)))
                {
                    context.Fail("The portal link is no longer valid.");
                }
            },
        };
    }
}
