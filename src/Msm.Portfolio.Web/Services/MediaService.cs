using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Msm.Portfolio.Web.Configuration;
using Msm.Portfolio.Web.Data;
using Msm.Portfolio.Web.Domain.Entities;
using Msm.Portfolio.Web.Domain.Enums;
using Msm.Portfolio.Web.Storage;

namespace Msm.Portfolio.Web.Services;

/// <summary>Outcome of one file in a batch upload (specification section 42).</summary>
public record UploadOutcome(string Filename, bool Succeeded, Guid? AssetId = null, string? Error = null);

public interface IMediaService
{
    /// <summary>
    /// Uploads a batch. Each file succeeds or fails on its own, so one bad file does
    /// not force a retoucher to start the whole batch again (specification section 42).
    /// </summary>
    Task<IReadOnlyList<UploadOutcome>> UploadImagesAsync(
        Guid clientId,
        IReadOnlyList<IFormFile> files,
        Guid? uploadedByUserId,
        CancellationToken cancellationToken = default);

    Task<UploadOutcome> UploadSelfTapeAsync(
        Guid clientId,
        IFormFile file,
        Guid? uploadedByUserId,
        CancellationToken cancellationToken = default);

    Task<bool> RemoveSelfTapeAsync(Guid clientId, Guid? actingUserId, CancellationToken cancellationToken = default);

    /// <summary>Adds or removes an image from the public portfolio, respecting the 30-image limit.</summary>
    Task<(bool Succeeded, string? Error)> SetSelectedAsync(
        Guid clientId, Guid assetId, bool selected, Guid? actingUserId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds several images to the portfolio in one go, up to the 30-image limit.
    /// </summary>
    /// <remarks>
    /// Choosing a shoot's worth of photographs one button at a time is the slowest part
    /// of a retoucher's job. Returns how many were added, and says so when the limit cut
    /// the batch short.
    /// </remarks>
    Task<(int Added, string? Error)> SetSelectedManyAsync(
        Guid clientId,
        IReadOnlyList<Guid> assetIds,
        Guid? actingUserId,
        CancellationToken cancellationToken = default);

    Task<(bool Succeeded, string? Error)> SetFeaturedAsync(
        Guid clientId, Guid assetId, Guid? actingUserId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records which part of an image must survive being cropped, or clears it.
    /// </summary>
    /// <remarks>
    /// Percentages across and down, as a browser's object-position takes them. Pass null
    /// for both to go back to the default framing.
    /// </remarks>
    Task<(bool Succeeded, string? Error)> SetFocalPointAsync(
        Guid clientId,
        Guid assetId,
        int? x,
        int? y,
        Guid? actingUserId,
        CancellationToken cancellationToken = default);

    Task ReorderAsync(
        Guid clientId, IReadOnlyList<Guid> orderedAssetIds, Guid? actingUserId, CancellationToken cancellationToken = default);

    Task<bool> SoftDeleteAsync(
        Guid clientId, Guid assetId, Guid? actingUserId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Regenerates the web renditions for one photograph from the archived original.
    /// Returns false when there is nothing to rebuild from, or the original will not
    /// decode.
    /// </summary>
    /// <remarks>
    /// The originals are kept exactly as uploaded, so a photograph whose renditions are
    /// missing is not a lost photograph — only an unfinished one. Rebuilding is therefore
    /// always preferable to asking a retoucher to upload a shoot a second time.
    /// </remarks>
    Task<bool> RebuildVariantsAsync(Guid assetId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MediaAsset>> GetPoolAsync(Guid clientId, CancellationToken cancellationToken = default);

    Task<MediaAsset?> GetSelfTapeAsync(Guid clientId, CancellationToken cancellationToken = default);
}

/// <summary>
/// The rules governing a client's media pool and public portfolio
/// (specification sections 12, 13, 14 and 38).
/// </summary>
public class MediaService(
    ApplicationDbContext db,
    IMediaStorageService storage,
    IImageProcessor imageProcessor,
    IAuditService audit,
    IOptions<MediaOptions> mediaOptions,
    ILogger<MediaService> logger) : IMediaService
{
    public async Task<IReadOnlyList<UploadOutcome>> UploadImagesAsync(
        Guid clientId,
        IReadOnlyList<IFormFile> files,
        Guid? uploadedByUserId,
        CancellationToken cancellationToken = default)
    {
        var options = mediaOptions.Value;
        var outcomes = new List<UploadOutcome>(files.Count);

        var poolCount = await db.MediaAssets.CountAsync(
            m => m.ClientId == clientId && !m.IsDeleted && m.MediaType == MediaType.Image, cancellationToken);

        var nextOrder = await NextDisplayOrderAsync(clientId, cancellationToken);

        foreach (var file in files)
        {
            if (poolCount >= options.MediaPoolImageLimit)
            {
                outcomes.Add(new UploadOutcome(file.FileName, false,
                    Error: $"The library is full at {options.MediaPoolImageLimit} images."));
                continue;
            }

            var rejection = ValidateImage(file, options);
            if (rejection is not null)
            {
                outcomes.Add(new UploadOutcome(file.FileName, false, Error: rejection));
                continue;
            }

            try
            {
                var asset = await StoreImageAsync(clientId, file, uploadedByUserId, nextOrder, cancellationToken);

                if (asset is null)
                {
                    outcomes.Add(new UploadOutcome(file.FileName, false,
                        Error: "This file could not be read as an image."));
                    continue;
                }

                outcomes.Add(new UploadOutcome(file.FileName, true, asset.Id));
                poolCount++;
                nextOrder++;
            }
            catch (Exception ex)
            {
                // One failure must not abandon the rest of the batch.
                logger.LogError(ex, "Upload failed for {Filename}.", file.FileName);
                outcomes.Add(new UploadOutcome(file.FileName, false, Error: "This file could not be uploaded."));
            }
        }

        if (outcomes.Any(o => o.Succeeded))
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        return outcomes;
    }

    private async Task<MediaAsset?> StoreImageAsync(
        Guid clientId,
        IFormFile file,
        Guid? uploadedByUserId,
        int displayOrder,
        CancellationToken cancellationToken)
    {
        // Buffered so the image can be inspected, re-read for variants, and stored,
        // without depending on the upload stream being seekable.
        using var buffer = new MemoryStream();
        await using (var source = file.OpenReadStream())
        {
            await source.CopyToAsync(buffer, cancellationToken);
        }

        buffer.Position = 0;
        var details = imageProcessor.Inspect(buffer);

        // Content type is what the browser claimed. Decoding is what proves it is an
        // image, so a renamed executable is rejected here rather than on the extension.
        if (details is null)
        {
            return null;
        }

        var assetId = Guid.CreateVersion7();
        var extension = Path.GetExtension(file.FileName);
        var originalKey = MediaStorageKeys.ForClient(clientId, assetId, extension);

        buffer.Position = 0;
        var stored = await storage.UploadAsync(buffer, originalKey, file.ContentType, cancellationToken);

        // One pass over one decode: the renditions and the measurements together. Reading
        // the file a second time simply to measure it is what exhausted memory and dropped
        // uploads partway through a batch.
        buffer.Position = 0;
        var processed = imageProcessor.Process(buffer);

        // No renditions means no photograph anyone can look at. Recording the asset
        // anyway is worse than refusing it: the row exists, the grid shows a tile, and
        // every request for its thumbnail answers 404 for ever, with nothing on screen to
        // say why or anything the retoucher can do about it. Refused here, the file gets
        // a plain message and a Retry button.
        if (processed.Variants.Count == 0)
        {
            logger.LogWarning(
                "No renditions could be produced for {Filename}; the upload was refused.",
                file.FileName);

            // The original is already stored, so remove it rather than leave a file on
            // disk that nothing in the database refers to.
            await storage.DeleteAsync(originalKey, cancellationToken);

            return null;
        }

        await WriteVariantsAsync(originalKey, processed.Variants, cancellationToken);

        // A photograph that could not be measured is stored all the same: the figures only
        // feed a suggested starting selection, and an unmeasured photograph is ranked as
        // ordinary rather than buried.
        var quality = processed.Quality;

        var asset = new MediaAsset
        {
            Id = assetId,
            ClientId = clientId,
            StorageKey = originalKey,
            OriginalFilename = Path.GetFileName(file.FileName),
            MimeType = file.ContentType,
            FileSize = stored.FileSize,
            Width = details.Width,
            Height = details.Height,
            Orientation = details.Orientation,
            MediaType = MediaType.Image,
            UploadedByUserId = uploadedByUserId,
            DisplayOrder = displayOrder,
            Sharpness = quality?.Sharpness,
            Exposure = quality?.Exposure,
            Contrast = quality?.Contrast,
            Clipping = quality?.Clipping,
            RenditionVersion = ImageProcessor.RenditionVersion
        };

        db.MediaAssets.Add(asset);

        audit.Record(nameof(MediaAsset), assetId.ToString(), "MediaUploaded",
            userId: uploadedByUserId,
            newValue: $"{asset.OriginalFilename} ({details.Width}x{details.Height}, {details.Orientation})");

        return asset;
    }

    public async Task<UploadOutcome> UploadSelfTapeAsync(
        Guid clientId,
        IFormFile file,
        Guid? uploadedByUserId,
        CancellationToken cancellationToken = default)
    {
        var options = mediaOptions.Value;

        if (file.Length == 0)
        {
            return new UploadOutcome(file.FileName, false, Error: "That file is empty.");
        }

        if (file.Length > options.MaxVideoBytes)
        {
            return new UploadOutcome(file.FileName, false,
                Error: $"Self-tapes must be under {options.MaxVideoBytes / (1024 * 1024)}MB.");
        }

        if (!options.AllowedVideoContentTypes.Contains(file.ContentType, StringComparer.OrdinalIgnoreCase))
        {
            return new UploadOutcome(file.FileName, false, Error: "That video format is not supported.");
        }

        // Only one self-tape is kept. Replacing removes the previous file rather than
        // leaving orphaned data in storage (specification section 14).
        var existing = await GetSelfTapeAsync(clientId, cancellationToken);
        if (existing is not null)
        {
            await storage.DeleteAsync(existing.StorageKey, cancellationToken);
            existing.IsDeleted = true;
            existing.DeletedAt = DateTimeOffset.UtcNow;
        }

        var assetId = Guid.CreateVersion7();
        var key = MediaStorageKeys.ForClient(clientId, assetId, Path.GetExtension(file.FileName));

        await using (var source = file.OpenReadStream())
        {
            await storage.UploadAsync(source, key, file.ContentType, cancellationToken);
        }

        db.MediaAssets.Add(new MediaAsset
        {
            Id = assetId,
            ClientId = clientId,
            StorageKey = key,
            OriginalFilename = Path.GetFileName(file.FileName),
            MimeType = file.ContentType,
            FileSize = file.Length,
            MediaType = MediaType.SelfTape,
            UploadedByUserId = uploadedByUserId
        });

        audit.Record(nameof(MediaAsset), assetId.ToString(), "SelfTapeUploaded", userId: uploadedByUserId);

        await db.SaveChangesAsync(cancellationToken);

        return new UploadOutcome(file.FileName, true, assetId);
    }

    public async Task<bool> RemoveSelfTapeAsync(
        Guid clientId, Guid? actingUserId, CancellationToken cancellationToken = default)
    {
        var selfTape = await GetSelfTapeAsync(clientId, cancellationToken);

        if (selfTape is null)
        {
            return false;
        }

        await storage.DeleteAsync(selfTape.StorageKey, cancellationToken);
        selfTape.IsDeleted = true;
        selfTape.DeletedAt = DateTimeOffset.UtcNow;

        audit.Record(nameof(MediaAsset), selfTape.Id.ToString(), "SelfTapeRemoved", userId: actingUserId);
        await db.SaveChangesAsync(cancellationToken);

        return true;
    }

    public async Task<(bool Succeeded, string? Error)> SetSelectedAsync(
        Guid clientId,
        Guid assetId,
        bool selected,
        Guid? actingUserId,
        CancellationToken cancellationToken = default)
    {
        var images = await LoadImagesAsync(clientId, cancellationToken);
        var asset = images.FirstOrDefault(m => m.Id == assetId);

        if (asset is null)
        {
            return (false, "That image could not be found.");
        }

        if (asset.IsSelectedForPortfolio == selected)
        {
            return (true, null);
        }

        if (selected)
        {
            var limit = mediaOptions.Value.PortfolioImageLimit;

            if (images.Count(m => m.IsSelectedForPortfolio) >= limit)
            {
                return (false, $"A portfolio can show at most {limit} images.");
            }
        }

        asset.IsSelectedForPortfolio = selected;

        audit.Record(nameof(MediaAsset), assetId.ToString(),
            selected ? "MediaSelectedForPortfolio" : "MediaDeselectedFromPortfolio",
            userId: actingUserId);

        await EnsureFeaturedIsValidAsync(clientId, images, actingUserId, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);

        return (true, null);
    }

    public async Task<(int Added, string? Error)> SetSelectedManyAsync(
        Guid clientId,
        IReadOnlyList<Guid> assetIds,
        Guid? actingUserId,
        CancellationToken cancellationToken = default)
    {
        if (assetIds.Count == 0)
        {
            return (0, "Choose at least one photograph first.");
        }

        var images = await LoadImagesAsync(clientId, cancellationToken);
        var limit = mediaOptions.Value.PortfolioImageLimit;
        var alreadyOn = images.Count(m => m.IsSelectedForPortfolio);
        var room = limit - alreadyOn;

        if (room <= 0)
        {
            return (0, $"A portfolio can show at most {limit} images.");
        }

        // Taken in the order they were chosen, so a partial result is predictable rather
        // than whichever the database happened to return first.
        var toAdd = assetIds
            .Select(id => images.FirstOrDefault(m => m.Id == id))
            .Where(m => m is not null && !m.IsSelectedForPortfolio)
            .Take(room)
            .ToList();

        foreach (var asset in toAdd)
        {
            asset!.IsSelectedForPortfolio = true;

            audit.Record(nameof(MediaAsset), asset.Id.ToString(),
                "MediaSelectedForPortfolio", userId: actingUserId);
        }

        // Once, after the whole batch: the first selected image becomes the main one, and
        // doing this per image would rewrite it for every photograph added.
        await EnsureFeaturedIsValidAsync(clientId, images, actingUserId, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);

        var skipped = assetIds.Count - toAdd.Count;

        // Says so plainly when the limit cut the batch short, rather than silently
        // adding some and leaving the retoucher to count what happened.
        var error = skipped > 0
            ? $"Added {toAdd.Count}. The portfolio holds {limit} images, so {skipped} could not be added."
            : null;

        return (toAdd.Count, error);
    }

    public async Task<(bool Succeeded, string? Error)> SetFeaturedAsync(
        Guid clientId, Guid assetId, Guid? actingUserId, CancellationToken cancellationToken = default)
    {
        var images = await LoadImagesAsync(clientId, cancellationToken);
        var asset = images.FirstOrDefault(m => m.Id == assetId);

        if (asset is null)
        {
            return (false, "That image could not be found.");
        }

        // The featured image is the portfolio hero and the Model Board card, so it has
        // to be an image the portfolio actually shows (specification section 12).
        if (!asset.IsSelectedForPortfolio)
        {
            return (false, "Only an image on the portfolio can be the main image.");
        }

        await ApplyFeaturedAsync(clientId, images, asset, actingUserId, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);

        return (true, null);
    }

    public async Task<(bool Succeeded, string? Error)> SetFocalPointAsync(
        Guid clientId,
        Guid assetId,
        int? x,
        int? y,
        Guid? actingUserId,
        CancellationToken cancellationToken = default)
    {
        var images = await LoadImagesAsync(clientId, cancellationToken);
        var asset = images.FirstOrDefault(m => m.Id == assetId);

        if (asset is null)
        {
            return (false, "That image could not be found.");
        }

        // Both or neither. One coordinate on its own describes nothing, and storing it
        // would leave the other axis silently reading as centre.
        if (x is null != y is null)
        {
            return (false, "A focal point needs both a position across and a position down.");
        }

        if (x is not null && (x is < 0 or > 100 || y is < 0 or > 100))
        {
            return (false, "A focal point is a percentage between 0 and 100.");
        }

        asset.FocalPointX = x;
        asset.FocalPointY = y;

        audit.Record(nameof(MediaAsset), asset.Id.ToString(),
            x is null ? "MediaFocalPointCleared" : "MediaFocalPointSet", userId: actingUserId);

        await db.SaveChangesAsync(cancellationToken);

        return (true, null);
    }

    public async Task ReorderAsync(
        Guid clientId,
        IReadOnlyList<Guid> orderedAssetIds,
        Guid? actingUserId,
        CancellationToken cancellationToken = default)
    {
        var assets = await db.MediaAssets
            .Where(m => m.ClientId == clientId && !m.IsDeleted && m.MediaType == MediaType.Image)
            .ToDictionaryAsync(m => m.Id, cancellationToken);

        var order = 0;
        foreach (var id in orderedAssetIds)
        {
            // Ids that do not belong to this client are ignored rather than trusted.
            if (assets.TryGetValue(id, out var asset))
            {
                asset.DisplayOrder = order++;
            }
        }

        audit.Record(nameof(ClientProfile), clientId.ToString(), "MediaReordered", userId: actingUserId);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> SoftDeleteAsync(
        Guid clientId, Guid assetId, Guid? actingUserId, CancellationToken cancellationToken = default)
    {
        var images = await LoadImagesAsync(clientId, cancellationToken);
        var asset = images.FirstOrDefault(m => m.Id == assetId);

        if (asset is null)
        {
            return false;
        }

        // Soft delete only. The file stays in storage so a mistaken removal is
        // recoverable; permanent destruction is a Super Admin action.
        asset.IsDeleted = true;
        asset.DeletedAt = DateTimeOffset.UtcNow;
        asset.IsSelectedForPortfolio = false;

        audit.Record(nameof(MediaAsset), assetId.ToString(), "MediaRemoved",
            userId: actingUserId, oldValue: asset.OriginalFilename);

        await EnsureFeaturedIsValidAsync(clientId, images, actingUserId, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);

        return true;
    }

    /// <summary>
    /// At most this many photographs are rebuilt at once.
    /// </summary>
    /// <remarks>
    /// A grid of sixty thumbnails asks for sixty images at once. If every missing one
    /// started its own decode, a single page load would try to decode sixty photographs
    /// simultaneously and exhaust memory — turning a page that showed broken images into
    /// a server that fell over. Two at a time, for the same reason the uploader sends two
    /// at a time.
    /// </remarks>
    private static readonly SemaphoreSlim RebuildGate = new(2, 2);

    public async Task<bool> RebuildVariantsAsync(
        Guid assetId, CancellationToken cancellationToken = default)
    {
        var asset = await db.MediaAssets
            .FirstOrDefaultAsync(m => m.Id == assetId && !m.IsDeleted, cancellationToken);

        if (asset is null || asset.MediaType != MediaType.Image)
        {
            return false;
        }

        await RebuildGate.WaitAsync(cancellationToken);

        try
        {
            // Checked again inside the gate: sixty tiles asking at once means most of
            // them queue here, and by the time they are let through the first one
            // through has usually done the work already.
            //
            // The version has to agree as well as the file existing. A photograph rendered
            // under older, smaller targets has all its files present and is exactly the
            // one that needs redoing.
            if (asset.RenditionVersion >= ImageProcessor.RenditionVersion
                && await storage.ExistsAsync(
                    MediaStorageKeys.ForVariant(asset.StorageKey, MediaVariant.Thumbnail),
                    cancellationToken))
            {
                return true;
            }

            await using var original = await storage.GetAsync(asset.StorageKey, cancellationToken);

            if (original is null)
            {
                // Nothing to rebuild from. The photograph really is gone.
                logger.LogWarning(
                    "Cannot rebuild renditions for {AssetId}: the original is missing.", assetId);

                return false;
            }

            using var buffer = new MemoryStream();
            await original.CopyToAsync(buffer, cancellationToken);
            buffer.Position = 0;

            var processed = imageProcessor.Process(buffer);

            if (processed.Variants.Count == 0)
            {
                logger.LogWarning(
                    "Cannot rebuild renditions for {AssetId}: the original will not decode.", assetId);

                return false;
            }

            await WriteVariantsAsync(asset.StorageKey, processed.Variants, cancellationToken);

            asset.RenditionVersion = ImageProcessor.RenditionVersion;

            // Measurements are filled in at the same time when they were never taken,
            // so a library uploaded before measuring existed starts ranking properly the
            // first time its photographs are looked at.
            if (!asset.HasBeenMeasured && processed.Quality is { } quality)
            {
                asset.Sharpness = quality.Sharpness;
                asset.Exposure = quality.Exposure;
                asset.Contrast = quality.Contrast;
                asset.Clipping = quality.Clipping;
            }

            audit.Record(nameof(MediaAsset), assetId.ToString(), "MediaVariantsRebuilt");
            await db.SaveChangesAsync(cancellationToken);

            logger.LogInformation("Rebuilt the renditions for {AssetId}.", assetId);

            return true;
        }
        finally
        {
            RebuildGate.Release();
        }
    }

    private async Task WriteVariantsAsync(
        string originalKey,
        IReadOnlyList<GeneratedVariant> variants,
        CancellationToken cancellationToken)
    {
        foreach (var variant in variants)
        {
            using var variantStream = new MemoryStream(variant.Content);

            await storage.UploadAsync(
                variantStream,
                MediaStorageKeys.ForVariant(originalKey, variant.Variant),
                variant.ContentType,
                cancellationToken);
        }
    }

    public async Task<IReadOnlyList<MediaAsset>> GetPoolAsync(
        Guid clientId, CancellationToken cancellationToken = default) =>
        await db.MediaAssets
            .Where(m => m.ClientId == clientId && !m.IsDeleted && m.MediaType == MediaType.Image)
            .OrderBy(m => m.DisplayOrder)
            .ThenBy(m => m.UploadedAt)
            .ToListAsync(cancellationToken);

    public Task<MediaAsset?> GetSelfTapeAsync(Guid clientId, CancellationToken cancellationToken = default) =>
        db.MediaAssets
            .Where(m => m.ClientId == clientId && !m.IsDeleted && m.MediaType == MediaType.SelfTape)
            .OrderByDescending(m => m.UploadedAt)
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>
    /// Keeps the "exactly one featured image once the portfolio contains media" rule
    /// from specification section 38 true after any change.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The specification stores the featured image in two places: a flag on the asset
    /// and a foreign key on the portfolio. Both are maintained here and nowhere else,
    /// so they cannot drift apart.
    /// </para>
    /// <para>
    /// This works over the caller's already-tracked entities rather than issuing a
    /// fresh query. A query would run against the database and so would not see the
    /// selection or deletion the caller has just made but not yet saved, which would
    /// leave the portfolio one change behind.
    /// </para>
    /// </remarks>
    private async Task EnsureFeaturedIsValidAsync(
        Guid clientId,
        List<MediaAsset> images,
        Guid? actingUserId,
        CancellationToken cancellationToken)
    {
        var portfolio = await db.Portfolios.FirstOrDefaultAsync(p => p.ClientId == clientId, cancellationToken);

        var selected = images
            .Where(m => !m.IsDeleted && m.IsSelectedForPortfolio)
            .OrderBy(m => m.DisplayOrder)
            .ToList();

        if (selected.Count == 0)
        {
            foreach (var stale in images.Where(m => m.IsFeatured))
            {
                stale.IsFeatured = false;
            }

            if (portfolio is not null)
            {
                portfolio.FeaturedMediaId = null;
            }

            return;
        }

        var featured = selected.FirstOrDefault(m => m.IsFeatured);

        // Either nothing is featured yet, or the featured image was removed or
        // deselected. Promote the first portfolio image so the hero is never empty.
        if (featured is null)
        {
            await ApplyFeaturedAsync(clientId, images, selected[0], actingUserId, cancellationToken);
            return;
        }

        // Clear a flag left on an image that has since been deleted or deselected.
        foreach (var stale in images.Where(m => m.IsFeatured && m.Id != featured.Id))
        {
            stale.IsFeatured = false;
        }

        if (portfolio is not null)
        {
            portfolio.FeaturedMediaId = featured.Id;
        }
    }

    private async Task ApplyFeaturedAsync(
        Guid clientId,
        List<MediaAsset> images,
        MediaAsset asset,
        Guid? actingUserId,
        CancellationToken cancellationToken)
    {
        foreach (var previous in images.Where(m => m.IsFeatured))
        {
            previous.IsFeatured = false;
        }

        asset.IsFeatured = true;

        var portfolio = await db.Portfolios.FirstOrDefaultAsync(p => p.ClientId == clientId, cancellationToken);
        if (portfolio is not null)
        {
            portfolio.FeaturedMediaId = asset.Id;
        }

        audit.Record(nameof(MediaAsset), asset.Id.ToString(), "FeaturedImageChanged",
            userId: actingUserId, newValue: asset.OriginalFilename);
    }

    /// <summary>
    /// Loads the client's images as tracked entities. Scoped by client, so an asset id
    /// belonging to someone else is simply absent rather than acted upon
    /// (specification section 35). Bounded by the 60-image pool limit.
    /// </summary>
    private Task<List<MediaAsset>> LoadImagesAsync(Guid clientId, CancellationToken cancellationToken) =>
        db.MediaAssets
            .Where(m => m.ClientId == clientId && !m.IsDeleted && m.MediaType == MediaType.Image)
            .OrderBy(m => m.DisplayOrder)
            .ToListAsync(cancellationToken);

    private async Task<int> NextDisplayOrderAsync(Guid clientId, CancellationToken cancellationToken)
    {
        var assets = db.MediaAssets.Where(m => m.ClientId == clientId && !m.IsDeleted);

        return await assets.AnyAsync(cancellationToken)
            ? await assets.MaxAsync(m => m.DisplayOrder, cancellationToken) + 1
            : 0;
    }

    /// <summary>
    /// Cheap checks made before anything is read or written. Limits come from
    /// configuration rather than constants (specification section 13).
    /// </summary>
    private static string? ValidateImage(IFormFile file, MediaOptions options)
    {
        if (file.Length == 0)
        {
            return "That file is empty.";
        }

        if (file.Length > options.MaxImageBytes)
        {
            return $"Images must be under {options.MaxImageBytes / (1024 * 1024)}MB.";
        }

        if (!options.AllowedImageContentTypes.Contains(file.ContentType, StringComparer.OrdinalIgnoreCase))
        {
            return "That image format is not supported.";
        }

        return null;
    }
}
