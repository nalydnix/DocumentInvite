using System.Globalization;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using DocumentInvite.Api.Domain;

namespace DocumentInvite.Api.Features.Audit;

/// <summary>Hash-chain calculation: <c>Hash = SHA256(PrevHash || canonical JSON of the event)</c>.</summary>
public static class AuditHasher
{
    /// <summary>The fixed value the first event links to.</summary>
    public static readonly byte[] GenesisHash = SHA256.HashData("DocumentInvite.Audit.Genesis.v1"u8);

    public static byte[] ComputeHash(byte[] prevHash, AuditEvent auditEvent)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha.AppendData(prevHash);
        sha.AppendData(Canonicalise(auditEvent));
        return sha.GetHashAndReset();
    }

    /// <summary>
    /// Fixed field order, UTC ISO-8601 timestamp and explicit nulls. The identity <c>Id</c> is
    /// not part of it because it is unknown until insert; ordering is covered by the chain.
    /// </summary>
    public static byte[] Canonicalise(AuditEvent e)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = JavaScriptEncoder.Default }))
        {
            writer.WriteStartObject();
            writer.WriteString("occurredAt", e.OccurredAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
            writer.WriteString("actorType", e.ActorType.ToString());
            writer.WriteString("actorId", e.ActorId?.ToString("D"));
            writer.WriteString("action", e.Action);
            writer.WriteString("targetType", e.TargetType);
            writer.WriteString("targetId", e.TargetId?.ToString("D"));
            writer.WriteString("ipAddress", e.IpAddress);
            writer.WriteString("userAgent", e.UserAgent);
            writer.WriteString("correlationId", e.CorrelationId);
            writer.WriteString("details", e.DetailsJson);
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }
}
