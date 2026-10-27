using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Options;

namespace DocumentInvite.Api.Infrastructure.Blob;

/// <summary>
/// Sets the storage account CORS rule that browser uploads (<c>PUT</c> with a SAS URL) need, when
/// <c>Blob:CorsAllowedOrigins</c> lists the web origins. Meant for Azurite in development, where nothing else
/// provisions the rule; leave the setting empty for a real account that is provisioned separately.
/// </summary>
public sealed class BlobCorsConfigurator(
    IOptions<BlobOptions> options, ILogger<BlobCorsConfigurator> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var value = options.Value;
        var origins = value.CorsAllowedOrigins.Where(o => !string.IsNullOrWhiteSpace(o)).ToList();
        if (origins.Count == 0 || string.IsNullOrWhiteSpace(value.ConnectionString))
        {
            return;
        }

        try
        {
            var service = new BlobServiceClient(value.ConnectionString);
            var properties = (await service.GetPropertiesAsync(cancellationToken)).Value;
            properties.Cors =
            [
                new BlobCorsRule
                {
                    AllowedOrigins = string.Join(',', origins),
                    AllowedMethods = "PUT,GET,HEAD,OPTIONS",
                    AllowedHeaders = "x-ms-*,content-type",
                    ExposedHeaders = "x-ms-*,ETag",
                    MaxAgeInSeconds = 3600,
                },
            ];
            await service.SetPropertiesAsync(properties, cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e, "Could not set the blob storage CORS rule; browser uploads may fail.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
