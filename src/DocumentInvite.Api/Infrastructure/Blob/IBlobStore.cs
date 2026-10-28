namespace DocumentInvite.Api.Infrastructure.Blob;

/// <summary>A short-lived URL for one blob.</summary>
public sealed record BlobAccessUrl(Uri Url, DateTimeOffset ExpiresAt);

public sealed record BlobInfo(long SizeBytes, string ContentType);

/// <summary>Wraps the private blob container that holds every uploaded file.</summary>
public interface IBlobStore
{
    /// <summary>A write-only SAS (create and write) for one blob. Browsers PUT to it directly.</summary>
    Task<BlobAccessUrl> CreateUploadUrlAsync(string blobName, TimeSpan lifetime, CancellationToken ct = default);

    /// <summary>A read-only SAS for one blob. <paramref name="downloadFileName"/> sets the download name.</summary>
    Task<BlobAccessUrl> CreateReadUrlAsync(string blobName, TimeSpan lifetime, string? downloadFileName, string? contentType, CancellationToken ct = default);

    /// <summary>The blob's size and content type, or null when it does not exist.</summary>
    Task<BlobInfo?> GetInfoAsync(string blobName, CancellationToken ct = default);

    Task<Stream> OpenReadAsync(string blobName, CancellationToken ct = default);

    /// <summary>Creates or replaces a blob.</summary>
    Task WriteAsync(string blobName, Stream content, string contentType, CancellationToken ct = default);

    Task DeleteAsync(string blobName, CancellationToken ct = default);
}
