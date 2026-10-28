namespace DocumentInvite.Api.Features.Files;

/// <summary>The upload limits shared by the slot and completion services.</summary>
public static class FileLimits
{
    public const long MaxBytes = 25L * 1024 * 1024;
    public static readonly TimeSpan UploadUrlLifetime = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan ReadUrlLifetime = TimeSpan.FromMinutes(5);
}

/// <summary>One allowed upload type.</summary>
public sealed record AllowedFileType(string ContentType, string Extension, string[] Extensions, byte[]? Signature)
{
    public bool IsImage => ContentType is FileTypes.Jpeg or FileTypes.Png;
}

/// <summary>The allow-list: PDF, JPG, PNG, DOCX, XLSX and CSV.</summary>
public static class FileTypes
{
    public const string Pdf = "application/pdf";
    public const string Jpeg = "image/jpeg";
    public const string Png = "image/png";
    public const string Docx = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
    public const string Xlsx = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    public const string Csv = "text/csv";

    private static readonly byte[] Zip = [0x50, 0x4B, 0x03, 0x04];

    private static readonly Dictionary<string, AllowedFileType> ByContentType = new[]
    {
        new AllowedFileType(Pdf, "pdf", ["pdf"], "%PDF-"u8.ToArray()),
        new AllowedFileType(Jpeg, "jpg", ["jpg", "jpeg"], [0xFF, 0xD8, 0xFF]),
        new AllowedFileType(Png, "png", ["png"], [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]),
        new AllowedFileType(Docx, "docx", ["docx"], Zip),
        new AllowedFileType(Xlsx, "xlsx", ["xlsx"], Zip),
        new AllowedFileType(Csv, "csv", ["csv"], null),
    }.ToDictionary(t => t.ContentType, StringComparer.Ordinal);

    /// <summary>Finds the type for a content type header value (parameters such as charset are ignored).</summary>
    public static AllowedFileType? Find(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return null;
        }

        var bare = contentType.Split(';', 2)[0].Trim().ToLowerInvariant();
        return ByContentType.GetValueOrDefault(bare);
    }

    /// <summary>True when the file name's extension is one of the type's extensions.</summary>
    public static bool ExtensionMatches(AllowedFileType type, string fileName)
    {
        var extension = Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();
        return type.Extensions.Contains(extension);
    }

    /// <summary>True when the content starts with the type's signature. Types without one always match.</summary>
    public static bool SignatureMatches(AllowedFileType type, ReadOnlySpan<byte> content) =>
        type.Signature is null || content.StartsWith(type.Signature);
}
