using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using DocumentInvite.Api.Features.Outbox;
using Microsoft.Extensions.Options;

namespace DocumentInvite.Api.Infrastructure.Blob;

/// <summary>
/// Sets the storage account CORS rule that browser uploads (<c>PUT</c> with a SAS URL) need, when
/// <c>Blob:CorsAllowedOrigins</c> lists the web origins or, in Development, <c>Portal:BaseUrl</c> names the web
/// origin. Meant for Azurite, where nothing else provisions the rule; leave both empty for a real account that is
/// provisioned separately.
/// </summary>
public sealed class BlobCorsConfigurator(
    IOptions<BlobOptions> options,
    IOptions<PortalOptions> portal,
    IHostEnvironment environment,
    ILogger<BlobCorsConfigurator> logger) : IHostedService
{
    /// <summary>
    /// The origins to allow: the explicit list, plus the configured web origin (<c>Portal:BaseUrl</c>) when asked, so a
    /// web app on any port works without a second setting. Each is reduced to scheme, host and port.
    /// </summary>
    public static IReadOnlyList<string> ResolveOrigins(
        IEnumerable<string> explicitOrigins, string? webBaseUrl, bool includeWebBaseUrl)
    {
        var candidates = includeWebBaseUrl ? explicitOrigins.Append(webBaseUrl ?? string.Empty) : explicitOrigins;
        return candidates
            .Select(o => o?.Trim() ?? string.Empty)
            .Where(o => Uri.TryCreate(o, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            .Select(o => new Uri(o).GetLeftPart(UriPartial.Authority))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>The rule browser uploads need.</summary>
    public static BlobCorsRule BuildRule(IEnumerable<string> origins) => new()
    {
        AllowedOrigins = string.Join(',', origins),
        AllowedMethods = "PUT,GET,HEAD,OPTIONS",
        AllowedHeaders = "x-ms-*,content-type",
        ExposedHeaders = "x-ms-*,ETag",
        MaxAgeInSeconds = 3600,
    };

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var value = options.Value;
        var origins = ResolveOrigins(value.CorsAllowedOrigins, portal.Value.BaseUrl, environment.IsDevelopment());
        if (origins.Count == 0 || string.IsNullOrWhiteSpace(value.ConnectionString))
        {
            return;
        }

        try
        {
            var service = new BlobServiceClient(value.ConnectionString);
            var properties = (await service.GetPropertiesAsync(cancellationToken)).Value;
            properties.Cors = [BuildRule(origins)];
            await service.SetPropertiesAsync(properties, cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e, "Could not set the blob storage CORS rule; browser uploads may fail.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
