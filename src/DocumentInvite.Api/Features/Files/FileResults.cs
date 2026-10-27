namespace DocumentInvite.Api.Features.Files;

public enum FileError
{
    ItemNotFound,
    NotAFileItem,
    FileNotFound,
    UnsupportedType,
    TooLarge,
    Empty,
    NotUploaded,
    SizeMismatch,
    TypeMismatch,
    InvalidContent,
    NotReady,
}

public sealed record FileResult<T>(T? Value, FileError? Error)
{
    public bool Succeeded => Error is null;

    public static FileResult<T> Ok(T value) => new(value, null);
    public static FileResult<T> Fail(FileError error) => new(default, error);
}

/// <summary>What the browser needs to upload one file: PUT the bytes to <see cref="UploadUrl"/>.</summary>
/// <param name="RequiredHeaders">Headers the PUT must send (<c>x-ms-blob-type: BlockBlob</c>).</param>
public sealed record UploadSlot(
    Guid FileId, Uri UploadUrl, DateTimeOffset ExpiresAt, IReadOnlyDictionary<string, string> RequiredHeaders);

public sealed record CompletedFile(
    Guid FileId, string StoredFileName, string ContentType, long SizeBytes, string Sha256, int Sequence);

public sealed record FileReadUrl(Uri Url, DateTimeOffset ExpiresAt, string FileName, string ContentType);
