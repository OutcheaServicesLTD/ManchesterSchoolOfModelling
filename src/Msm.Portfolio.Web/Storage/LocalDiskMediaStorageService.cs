using Microsoft.Extensions.Options;
using Msm.Portfolio.Web.Configuration;

namespace Msm.Portfolio.Web.Storage;

/// <summary>
/// Stores media on local disk. Suitable for development and a single-server
/// deployment; object storage replaces it behind the same interface when MSM's
/// hosting is decided (specification section 33).
/// </summary>
public class LocalDiskMediaStorageService : IMediaStorageService
{
    private readonly string _root;
    private readonly ILogger<LocalDiskMediaStorageService> _logger;

    public LocalDiskMediaStorageService(
        IOptions<MediaOptions> options,
        IHostEnvironment environment,
        ILogger<LocalDiskMediaStorageService> logger)
    {
        _logger = logger;

        var configured = options.Value.LocalStorageRoot;

        _root = Path.IsPathRooted(configured)
            ? configured
            : Path.Combine(environment.ContentRootPath, configured);

        Directory.CreateDirectory(_root);
    }

    public async Task<StoredMedia> UploadAsync(
        Stream content,
        string storageKey,
        string contentType,
        CancellationToken cancellationToken = default)
    {
        var path = ResolvePath(storageKey);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        await using (var file = File.Create(path))
        {
            await content.CopyToAsync(file, cancellationToken);
        }

        var size = new FileInfo(path).Length;
        _logger.LogDebug("Stored {Key} ({Size} bytes).", storageKey, size);

        return new StoredMedia(storageKey, size, contentType);
    }

    public Task<Stream?> GetAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        var path = ResolvePath(storageKey);

        if (!File.Exists(path))
        {
            return Task.FromResult<Stream?>(null);
        }

        Stream stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 64 * 1024, useAsync: true);

        return Task.FromResult<Stream?>(stream);
    }

    public Task DeleteAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        var path = ResolvePath(storageKey);

        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    public Task<bool> ExistsAsync(string storageKey, CancellationToken cancellationToken = default) =>
        Task.FromResult(File.Exists(ResolvePath(storageKey)));

    /// <summary>
    /// Maps a storage key to a path inside the storage root.
    /// </summary>
    /// <remarks>
    /// Keys are built by the application, but this still refuses anything that escapes
    /// the root. A key reaching here with "../" in it would otherwise read or overwrite
    /// arbitrary files on the server, and that is too severe a failure to leave resting
    /// on the assumption that every future caller builds keys correctly.
    /// </remarks>
    private string ResolvePath(string storageKey)
    {
        if (string.IsNullOrWhiteSpace(storageKey))
        {
            throw new ArgumentException("A storage key is required.", nameof(storageKey));
        }

        var combined = Path.GetFullPath(Path.Combine(_root, storageKey));
        var rootWithSeparator = _root.EndsWith(Path.DirectorySeparatorChar)
            ? _root
            : _root + Path.DirectorySeparatorChar;

        if (!combined.StartsWith(rootWithSeparator, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Storage key '{storageKey}' resolves outside the media root.");
        }

        return combined;
    }
}
