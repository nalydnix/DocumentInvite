using DocumentInvite.Api.Infrastructure.Blob;
using DocumentInvite.Api.Infrastructure.Pdf;

namespace DocumentInvite.Api.Features.Files;

public static class FilesServiceCollectionExtensions
{
    /// <summary>The secure file pipeline: blob storage, upload slots, completion and read URLs.</summary>
    public static IServiceCollection AddFiles(this IServiceCollection services)
    {
        services.AddOptions<BlobOptions>().BindConfiguration(BlobOptions.Section);
        services.AddSingleton<IBlobStore, AzureBlobStore>();
        services.AddHostedService<BlobCorsConfigurator>();
        services.AddSingleton<IImagePdfConverter, QuestPdfImageConverter>();
        services.AddScoped<UploadSlotService>();
        services.AddScoped<FileCompletionService>();
        services.AddScoped<FileReadService>();
        return services;
    }
}
