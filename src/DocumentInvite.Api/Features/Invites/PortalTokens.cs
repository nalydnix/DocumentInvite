using System.Security.Cryptography;
using System.Text;
using DocumentInvite.Api.Domain;
using DocumentInvite.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace DocumentInvite.Api.Features.Invites;

/// <summary>Creation and hashing of portal tokens. Only the hash is ever stored.</summary>
public static class PortalTokens
{
    public const int TokenBytes = 32;

    /// <summary>32 random bytes, base64url encoded without padding.</summary>
    public static string Generate() => WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(TokenBytes));

    public static byte[] Hash(string token)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);
        return SHA256.HashData(Encoding.UTF8.GetBytes(token));
    }
}

public enum TokenLookupFailure
{
    NotFound,
    Revoked,
    Expired,
}

public sealed record TokenLookupResult(Invite? Invite, TokenLookupFailure? Failure)
{
    public bool IsValid => Invite is not null;
}

/// <summary>Finds the invite a portal token belongs to, matching on the hash only.</summary>
public sealed class InviteTokenService(AppDbContext db, TimeProvider clock)
{
    public async Task<TokenLookupResult> ResolveAsync(string? token, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(token))
        {
            return new TokenLookupResult(null, TokenLookupFailure.NotFound);
        }

        var hash = PortalTokens.Hash(token);
        var invite = await db.Invites.FirstOrDefaultAsync(i => i.TokenHash == hash, ct);
        if (invite is null)
        {
            return new TokenLookupResult(null, TokenLookupFailure.NotFound);
        }

        if (invite.RevokedAt is not null)
        {
            return new TokenLookupResult(null, TokenLookupFailure.Revoked);
        }

        if (invite.TokenExpiresAt <= clock.GetUtcNow())
        {
            return new TokenLookupResult(null, TokenLookupFailure.Expired);
        }

        return new TokenLookupResult(invite, null);
    }
}
