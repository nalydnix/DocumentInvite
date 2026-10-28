using System.Text.Json.Serialization;

namespace DocumentInvite.Api.Domain;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum StoredFileStatus
{
    PendingUpload,
    Ready,
    Superseded,
}

/// <summary>A file a party uploaded for a request item. The bytes live in blob storage.</summary>
public class StoredFile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RequestItemId { get; set; }

    /// <summary>Opaque blob path, <c>invites/{inviteId}/{fileId}</c>. It never contains personal data.</summary>
    public string BlobName { get; set; } = string.Empty;

    public string OriginalFileName { get; set; } = string.Empty;

    /// <summary>The canonical download name, assigned when the upload completes.</summary>
    public string? StoredFileName { get; set; }

    public string ContentType { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public byte[]? Sha256 { get; set; }

    /// <summary>The 1-based sequence number within the item, assigned when the upload completes.</summary>
    public int? Sequence { get; set; }

    public StoredFileStatus Status { get; set; } = StoredFileStatus.PendingUpload;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UploadedAt { get; set; }
}
