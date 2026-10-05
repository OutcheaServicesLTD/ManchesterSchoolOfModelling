namespace Msm.Portfolio.Web.Storage;

/// <summary>A file that has been written to storage.</summary>
public record StoredMedia(string StorageKey, long FileSize, string ContentType);

/// <summary>
/// Where media files live, kept separate from the relational database
/// (specification section 33).
/// </summary>
/// <remarks>
/// The provider is still an open decision. Nothing above this interface knows whether
/// files sit on local disk, Azure Blob, S3 or R2, so swapping implementation is a
/// registration change rather than a code change.
/// </remarks>
public interface IMediaStorageService
{
    Task<StoredMedia> UploadAsync(
        Stream content,
        string storageKey,
        string contentType,
        CancellationToken cancellationToken = default);

    Task<Stream?> GetAsync(string storageKey, CancellationToken cancellationToken = default);

    Task DeleteAsync(string storageKey, CancellationToken cancellationToken = default);

    Task<bool> ExistsAsync(string storageKey, CancellationToken cancellationToken = default);
}

/// <summary>
/// The rendition of an image being requested. Originals are archived untouched and are
/// never served to a browser (specification section 13).
/// </summary>
public enum MediaVariant
{
    Original = 0,
    Large = 1,
    Medium = 2,
    Thumbnail = 3
}

/// <summary>
/// Builds storage keys. Variants are derived from the original's key by convention,
/// so one key on the asset row locates every rendition of it.
/// </summary>
public static class MediaStorageKeys
{
    public static string ForClient(Guid clientId, Guid assetId, string extension) =>
        $"clients/{clientId:N}/{assetId:N}/original{Normalise(extension)}";

    /// <summary>
    /// Web renditions are always JPEG, so the key is predictable regardless of what
    /// the photographer uploaded.
    /// </summary>
    public static string ForVariant(string originalKey, MediaVariant variant)
    {
        if (variant == MediaVariant.Original)
        {
            return originalKey;
        }

        var directory = originalKey[..originalKey.LastIndexOf('/')];
        return $"{directory}/{variant.ToString().ToLowerInvariant()}.jpg";
    }

    private static string Normalise(string extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return string.Empty;
        }

        return extension.StartsWith('.') ? extension.ToLowerInvariant() : $".{extension.ToLowerInvariant()}";
    }
}
