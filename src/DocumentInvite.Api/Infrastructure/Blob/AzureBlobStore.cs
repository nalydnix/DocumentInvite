using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using Microsoft.Extensions.Options;

namespace DocumentInvite.Api.Infrastructure.Blob;

/// <summary>Azure Blob Storage (or Azurite) with a private container.</summary>
public sealed class AzureBlobStore : IBlobStore
{
    // Allows for small clock differences between this server and the storage service.
    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(1);

    private readonly BlobContainerClient _container;
    private readonly TimeProvider _clock;
    private readonly bool _createContainer;
    private int _ensured;

    public AzureBlobStore(IOptions<BlobOptions> options, TimeProvider clock)
    {
        var value = options.Value;
        if (string.IsNullOrWhiteSpace(value.ConnectionString))
        {
            throw new InvalidOperationException("Blob:ConnectionString must be configured.");
        }

        _container = new BlobContainerClient(value.ConnectionString, value.ContainerName);
        _clock = clock;
        _createContainer = value.CreateContainerIfMissing;
    }

    public async Task<BlobAccessUrl> CreateUploadUrlAsync(string blobName, TimeSpan lifetime, CancellationToken ct = default)
    {
        await EnsureContainerAsync(ct);
        var expires = _clock.GetUtcNow() + lifetime;
        var sas = NewSas(blobName, expires, BlobSasPermissions.Create | BlobSasPermissions.Write);
        return new BlobAccessUrl(_container.GetBlobClient(blobName).GenerateSasUri(sas), expires);
    }

    public Task<BlobAccessUrl> CreateReadUrlAsync(
        string blobName, TimeSpan lifetime, string? downloadFileName, string? contentType, CancellationToken ct = default)
    {
        var expires = _clock.GetUtcNow() + lifetime;
        var sas = NewSas(blobName, expires, BlobSasPermissions.Read);
        if (downloadFileName is not null)
        {
            sas.ContentDisposition = $"attachment; filename=\"{downloadFileName}\"";
        }

        if (contentType is not null)
        {
            sas.ContentType = contentType;
        }

        return Task.FromResult(new BlobAccessUrl(_container.GetBlobClient(blobName).GenerateSasUri(sas), expires));
    }

    public async Task<BlobInfo?> GetInfoAsync(string blobName, CancellationToken ct = default)
    {
        try
        {
            var props = (await _container.GetBlobClient(blobName).GetPropertiesAsync(cancellationToken: ct)).Value;
            return new BlobInfo(props.ContentLength, props.ContentType);
        }
        catch (Azure.RequestFailedException e) when (e.Status == 404)
        {
            return null;
        }
    }

    public async Task<Stream> OpenReadAsync(string blobName, CancellationToken ct = default) =>
        await _container.GetBlobClient(blobName).OpenReadAsync(cancellationToken: ct);

    public async Task WriteAsync(string blobName, Stream content, string contentType, CancellationToken ct = default)
    {
        await EnsureContainerAsync(ct);
        await _container.GetBlobClient(blobName).UploadAsync(
            content,
            new BlobUploadOptions { HttpHeaders = new BlobHttpHeaders { ContentType = contentType } },
            ct);
    }

    public async Task DeleteAsync(string blobName, CancellationToken ct = default) =>
        await _container.GetBlobClient(blobName).DeleteIfExistsAsync(cancellationToken: ct);

    private BlobSasBuilder NewSas(string blobName, DateTimeOffset expires, BlobSasPermissions permissions)
    {
        var sas = new BlobSasBuilder
        {
            BlobContainerName = _container.Name,
            BlobName = blobName,
            Resource = "b",
            StartsOn = _clock.GetUtcNow() - ClockSkew,
            ExpiresOn = expires,
        };
        sas.SetPermissions(permissions);
        return sas;
    }

    private async Task EnsureContainerAsync(CancellationToken ct)
    {
        if (_createContainer && Interlocked.CompareExchange(ref _ensured, 1, 0) == 0)
        {
            try
            {
                // Created private: no public access level is given.
                await _container.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: ct);
            }
            catch
            {
                Interlocked.Exchange(ref _ensured, 0);
                throw;
            }
        }
    }
}
