using System.Collections.Concurrent;
using DocumentInvite.Api.Infrastructure.Blob;

namespace DocumentInvite.Api.Tests.Files;

/// <summary>In-memory <see cref="IBlobStore"/>. A test "uploads" by calling <see cref="Put"/>.</summary>
public sealed class FakeBlobStore : IBlobStore
{
    private readonly ConcurrentDictionary<string, (byte[] Content, string ContentType)> _blobs = new();
    private readonly TimeProvider _clock;

    public FakeBlobStore(TimeProvider? clock = null) => _clock = clock ?? TimeProvider.System;

    public List<(string BlobName, TimeSpan Lifetime)> UploadUrls { get; } = [];
    public List<(string BlobName, TimeSpan Lifetime, string? DownloadName)> ReadUrls { get; } = [];

    public void Put(string blobName, byte[] content, string contentType) => _blobs[blobName] = (content, contentType);

    public byte[]? Get(string blobName) => _blobs.TryGetValue(blobName, out var b) ? b.Content : null;

    public string? ContentTypeOf(string blobName) => _blobs.TryGetValue(blobName, out var b) ? b.ContentType : null;

    public bool Exists(string blobName) => _blobs.ContainsKey(blobName);

    public Task<BlobAccessUrl> CreateUploadUrlAsync(string blobName, TimeSpan lifetime, CancellationToken ct = default)
    {
        UploadUrls.Add((blobName, lifetime));
        return Task.FromResult(new BlobAccessUrl(new Uri($"https://fake.blob/{blobName}?sp=cw"), _clock.GetUtcNow() + lifetime));
    }

    public Task<BlobAccessUrl> CreateReadUrlAsync(
        string blobName, TimeSpan lifetime, string? downloadFileName, string? contentType, CancellationToken ct = default)
    {
        ReadUrls.Add((blobName, lifetime, downloadFileName));
        return Task.FromResult(new BlobAccessUrl(new Uri($"https://fake.blob/{blobName}?sp=r"), _clock.GetUtcNow() + lifetime));
    }

    public Task<BlobInfo?> GetInfoAsync(string blobName, CancellationToken ct = default) =>
        Task.FromResult(_blobs.TryGetValue(blobName, out var b) ? new BlobInfo(b.Content.Length, b.ContentType) : null);

    public Task<Stream> OpenReadAsync(string blobName, CancellationToken ct = default) =>
        Task.FromResult<Stream>(new MemoryStream(_blobs[blobName].Content, writable: false));

    public async Task WriteAsync(string blobName, Stream content, string contentType, CancellationToken ct = default)
    {
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, ct);
        _blobs[blobName] = (buffer.ToArray(), contentType);
    }

    public Task DeleteAsync(string blobName, CancellationToken ct = default)
    {
        _blobs.TryRemove(blobName, out _);
        return Task.CompletedTask;
    }
}
