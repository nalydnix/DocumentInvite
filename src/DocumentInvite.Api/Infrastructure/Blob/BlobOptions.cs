namespace DocumentInvite.Api.Infrastructure.Blob;

public sealed class BlobOptions
{
    public const string Section = "Blob";

    /// <summary>The storage account connection string. It must include the account key, which signs the SAS URLs.</summary>
    public string ConnectionString { get; set; } = string.Empty;

    public string ContainerName { get; set; } = "documents";

    /// <summary>Creates the private container on first use. Turn off when it is provisioned separately.</summary>
    public bool CreateContainerIfMissing { get; set; } = true;

    /// <summary>
    /// Web origins allowed to upload straight to storage. When set, the API applies the CORS rule at startup (for Azurite).
    /// Leave empty when the storage account is provisioned separately.
    /// </summary>
    public string[] CorsAllowedOrigins { get; set; } = [];
}
