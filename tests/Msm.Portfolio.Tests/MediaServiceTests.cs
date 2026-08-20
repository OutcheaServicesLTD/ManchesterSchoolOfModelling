using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Msm.Portfolio.Web.Configuration;
using Msm.Portfolio.Web.Data;
using Msm.Portfolio.Web.Domain.Entities;
using Msm.Portfolio.Web.Domain.Enums;
using Msm.Portfolio.Web.Services;
using Msm.Portfolio.Web.Storage;
using SkiaSharp;

namespace Msm.Portfolio.Tests;

/// <summary>
/// Exercised against a real SQLite database rather than a fake, so the counting and
/// filtering the limits depend on are genuinely executed as queries.
/// </summary>
public class MediaServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _db;
    private readonly MediaService _service;
    private readonly InMemoryStorage _storage = new();
    private readonly Guid _clientId = Guid.CreateVersion7();

    private static readonly MediaOptions Options = new()
    {
        MediaPoolImageLimit = 6,
        PortfolioImageLimit = 3,
        MaxImageBytes = 5 * 1024 * 1024,
        MaxVideoBytes = 10 * 1024 * 1024,
        AllowedImageContentTypes = ["image/jpeg", "image/png"],
        AllowedVideoContentTypes = ["video/mp4"]
    };

    public MediaServiceTests()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();

        _db = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();

        var user = new ApplicationUser { Id = Guid.CreateVersion7(), UserName = "m@example.com", Email = "m@example.com" };
        _db.Users.Add(user);
        _db.ClientProfiles.Add(new ClientProfile
        {
            Id = _clientId, ApplicationUserId = user.Id, FirstName = "Test", LastName = "Model"
        });
        _db.Portfolios.Add(new Msm.Portfolio.Web.Domain.Entities.Portfolio { ClientId = _clientId });
        _db.SaveChanges();

        _service = new MediaService(
            _db,
            _storage,
            new ImageProcessor(NullLogger<ImageProcessor>.Instance),
            new AuditService(_db),
            new OptionsWrapper<MediaOptions>(Options),
            NullLogger<MediaService>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private static IFormFile Jpeg(string name, int width = 800, int height = 1200)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap)) { canvas.Clear(SKColors.Teal); }
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 85);

        var stream = new MemoryStream(data.ToArray());
        return new FormFile(stream, 0, stream.Length, "files", name)
        {
            Headers = new HeaderDictionary(),
            ContentType = "image/jpeg"
        };
    }

    private static IFormFile Raw(string name, string contentType, int bytes)
    {
        var stream = new MemoryStream(new byte[bytes]);
        return new FormFile(stream, 0, stream.Length, "file", name)
        {
            Headers = new HeaderDictionary(),
            ContentType = contentType
        };
    }

    private async Task<List<Guid>> UploadAsync(int count)
    {
        var files = Enumerable.Range(0, count).Select(i => Jpeg($"shot{i}.jpg")).ToList();
        var outcomes = await _service.UploadImagesAsync(_clientId, files, null);
        return [.. outcomes.Where(o => o.Succeeded).Select(o => o.AssetId!.Value)];
    }

    [Fact]
    public async Task An_upload_records_dimensions_and_orientation()
    {
        var outcomes = await _service.UploadImagesAsync(_clientId, [Jpeg("portrait.jpg", 800, 1200)], null);

        Assert.True(outcomes.Single().Succeeded);

        var asset = await _db.MediaAssets.SingleAsync();
        Assert.Equal(800, asset.Width);
        Assert.Equal(1200, asset.Height);
        Assert.Equal(MediaOrientation.Portrait, asset.Orientation);
    }

    [Fact]
    public async Task An_upload_stores_the_original_and_all_three_renditions()
    {
        await _service.UploadImagesAsync(_clientId, [Jpeg("a.jpg")], null);

        var asset = await _db.MediaAssets.SingleAsync();

        Assert.True(_storage.Contains(asset.StorageKey));
        foreach (var variant in new[] { MediaVariant.Large, MediaVariant.Medium, MediaVariant.Thumbnail })
        {
            Assert.True(
                _storage.Contains(MediaStorageKeys.ForVariant(asset.StorageKey, variant)),
                $"{variant} was not stored.");
        }
    }

    [Fact]
    public async Task The_media_pool_is_capped_at_the_configured_limit()
    {
        await UploadAsync(Options.MediaPoolImageLimit);

        var outcomes = await _service.UploadImagesAsync(_clientId, [Jpeg("one-too-many.jpg")], null);

        Assert.False(outcomes.Single().Succeeded);
        Assert.Contains("full", outcomes.Single().Error);
        Assert.Equal(Options.MediaPoolImageLimit, await _db.MediaAssets.CountAsync());
    }

    /// <summary>
    /// Specification section 42: a rejected file must not force the retoucher to
    /// restart the whole batch.
    /// </summary>
    [Fact]
    public async Task A_bad_file_in_a_batch_does_not_stop_the_good_ones()
    {
        var batch = new List<IFormFile>
        {
            Jpeg("good1.jpg"),
            Raw("virus.exe", "application/x-msdownload", 100),
            Jpeg("good2.jpg"),
            Raw("huge.jpg", "image/jpeg", (int)Options.MaxImageBytes + 1),
            Jpeg("good3.jpg")
        };

        var outcomes = await _service.UploadImagesAsync(_clientId, batch, null);

        Assert.Equal(3, outcomes.Count(o => o.Succeeded));
        Assert.Equal(2, outcomes.Count(o => !o.Succeeded));
        Assert.Equal(3, await _db.MediaAssets.CountAsync());

        // Each failure is reported against its own filename.
        Assert.Contains(outcomes, o => o.Filename == "virus.exe" && !o.Succeeded);
        Assert.Contains(outcomes, o => o.Filename == "huge.jpg" && o.Error!.Contains("under"));
    }

    /// <summary>
    /// Content type is only what the browser claimed. A file that cannot be decoded is
    /// rejected however it is labelled.
    /// </summary>
    [Fact]
    public async Task A_non_image_labelled_as_a_jpeg_is_rejected()
    {
        var outcomes = await _service.UploadImagesAsync(
            _clientId, [Raw("payload.jpg", "image/jpeg", 2048)], null);

        Assert.False(outcomes.Single().Succeeded);
        Assert.Empty(await _db.MediaAssets.ToListAsync());
    }

    [Fact]
    public async Task The_portfolio_is_capped_at_the_configured_limit()
    {
        var ids = await UploadAsync(Options.PortfolioImageLimit + 1);

        foreach (var id in ids.Take(Options.PortfolioImageLimit))
        {
            Assert.True((await _service.SetSelectedAsync(_clientId, id, true, null)).Succeeded);
        }

        var (succeeded, error) = await _service.SetSelectedAsync(_clientId, ids[^1], true, null);

        Assert.False(succeeded);
        Assert.Contains("at most 3", error);
    }

    [Fact]
    public async Task Renditions_missing_from_storage_are_rebuilt_from_the_original()
    {
        // What this is for: a library of photographs whose renditions were never written
        // — a decode that ran out of memory, a deploy that landed mid-batch. The
        // originals are archived exactly as uploaded, so the photographs are not lost,
        // only unfinished, and asking for a shoot to be uploaded again would be asking
        // for work nobody needs to do.
        var ids = await UploadAsync(2);
        var assets = await _db.MediaAssets.Where(m => ids.Contains(m.Id)).ToListAsync();

        foreach (var asset in assets)
        {
            foreach (var variant in new[] { MediaVariant.Thumbnail, MediaVariant.Medium, MediaVariant.Large })
            {
                await _storage.DeleteAsync(MediaStorageKeys.ForVariant(asset.StorageKey, variant));
            }
        }

        Assert.False(_storage.Contains(
            MediaStorageKeys.ForVariant(assets[0].StorageKey, MediaVariant.Thumbnail)));

        Assert.True(await _service.RebuildVariantsAsync(ids[0]));

        Assert.True(_storage.Contains(
            MediaStorageKeys.ForVariant(assets[0].StorageKey, MediaVariant.Thumbnail)));
        Assert.True(_storage.Contains(
            MediaStorageKeys.ForVariant(assets[0].StorageKey, MediaVariant.Large)));

        // Only the one asked for: rebuilding is driven by what is actually requested.
        Assert.False(_storage.Contains(
            MediaStorageKeys.ForVariant(assets[1].StorageKey, MediaVariant.Thumbnail)));
    }

    [Fact]
    public async Task Renditions_made_under_older_targets_are_rebuilt()
    {
        // The case that matters: every file is present, so nothing looks broken, but they
        // were made at the old smaller sizes. A raised target that only applied to the
        // next upload would leave every portfolio already in the system as it was.
        var ids = await UploadAsync(1);
        var asset = await _db.MediaAssets.SingleAsync(m => m.Id == ids[0]);

        Assert.Equal(ImageProcessor.RenditionVersion, asset.RenditionVersion);

        asset.RenditionVersion = 0;
        await _db.SaveChangesAsync();

        Assert.True(await _service.RebuildVariantsAsync(ids[0]));

        var rebuilt = await _db.MediaAssets.SingleAsync(m => m.Id == ids[0]);
        Assert.Equal(ImageProcessor.RenditionVersion, rebuilt.RenditionVersion);
    }

    [Fact]
    public async Task Renditions_already_at_the_current_version_are_left_alone()
    {
        var ids = await UploadAsync(1);
        var asset = await _db.MediaAssets.SingleAsync(m => m.Id == ids[0]);
        var key = MediaStorageKeys.ForVariant(asset.StorageKey, MediaVariant.Large);

        // Stand-in content, so a rebuild that should not happen is detectable.
        await _storage.UploadAsync(new MemoryStream([1, 2, 3]), key, "image/jpeg");

        Assert.True(await _service.RebuildVariantsAsync(ids[0]));

        await using var stream = await _storage.GetAsync(key);
        using var buffer = new MemoryStream();
        await stream!.CopyToAsync(buffer);

        Assert.Equal(3, buffer.Length);
    }

    [Fact]
    public async Task Rebuilding_says_no_when_the_original_is_gone_too()
    {
        var ids = await UploadAsync(1);
        var asset = await _db.MediaAssets.SingleAsync(m => m.Id == ids[0]);

        await _storage.DeleteAsync(asset.StorageKey);
        await _storage.DeleteAsync(MediaStorageKeys.ForVariant(asset.StorageKey, MediaVariant.Thumbnail));

        Assert.False(await _service.RebuildVariantsAsync(ids[0]));
    }

    [Fact]
    public async Task Rebuilding_an_unknown_photograph_says_no_rather_than_throwing()
    {
        Assert.False(await _service.RebuildVariantsAsync(Guid.CreateVersion7()));
    }

    [Fact]
    public async Task Rebuilding_fills_in_measurements_that_were_never_taken()
    {
        // A library uploaded before measuring existed starts ranking properly the first
        // time its photographs are looked at, rather than staying unranked for ever.
        var ids = await UploadAsync(1);
        var asset = await _db.MediaAssets.SingleAsync(m => m.Id == ids[0]);

        asset.Sharpness = null;
        asset.Exposure = null;
        asset.Contrast = null;
        asset.Clipping = null;
        await _db.SaveChangesAsync();

        await _storage.DeleteAsync(MediaStorageKeys.ForVariant(asset.StorageKey, MediaVariant.Thumbnail));
        await _service.RebuildVariantsAsync(ids[0]);

        var measured = await _db.MediaAssets.SingleAsync(m => m.Id == ids[0]);
        Assert.NotNull(measured.Sharpness);
        Assert.NotNull(measured.Exposure);
    }

    [Fact]
    public async Task A_photograph_whose_renditions_cannot_be_made_is_refused_rather_than_stored()
    {
        // Storing it would create a row with no image behind it: a tile in the grid whose
        // thumbnail answers 404 for ever, with nothing on screen to say why. Refused, the
        // file gets a plain message and a Retry button.
        var service = new MediaService(
            _db,
            _storage,
            new RefusesToDecode(),
            new AuditService(_db),
            new OptionsWrapper<MediaOptions>(Options),
            NullLogger<MediaService>.Instance);

        var outcomes = await service.UploadImagesAsync(_clientId, [Jpeg("shot.jpg")], null);

        Assert.False(outcomes.Single().Succeeded);
        Assert.Equal(0, await _db.MediaAssets.CountAsync());

        // And nothing is left behind on disk that the database no longer refers to.
        Assert.Empty(_storage.Keys);
    }

    [Fact]
    public async Task Several_images_can_be_added_to_the_portfolio_at_once()
    {
        // Choosing a shoot's worth one at a time is the slowest part of the job.
        var ids = await UploadAsync(3);

        var (added, error) = await _service.SetSelectedManyAsync(_clientId, ids, null);

        Assert.Equal(3, added);
        Assert.Null(error);
        Assert.Equal(3, await _db.MediaAssets.CountAsync(m => m.IsSelectedForPortfolio));
    }

    [Fact]
    public async Task Adding_more_than_the_limit_adds_what_fits_and_says_what_did_not()
    {
        // Partial success has to be reported: silently adding some and dropping the rest
        // would leave a retoucher counting thumbnails to work out what happened.
        var ids = await UploadAsync(Options.PortfolioImageLimit + 2);

        var (added, error) = await _service.SetSelectedManyAsync(_clientId, ids, null);

        Assert.Equal(Options.PortfolioImageLimit, added);
        Assert.Contains("2 could not be added", error);
        Assert.Equal(
            Options.PortfolioImageLimit,
            await _db.MediaAssets.CountAsync(m => m.IsSelectedForPortfolio));
    }

    [Fact]
    public async Task Adding_a_batch_that_is_already_on_the_portfolio_changes_nothing()
    {
        var ids = await UploadAsync(2);
        await _service.SetSelectedManyAsync(_clientId, ids, null);

        var (added, _) = await _service.SetSelectedManyAsync(_clientId, ids, null);

        Assert.Equal(0, added);
        Assert.Equal(2, await _db.MediaAssets.CountAsync(m => m.IsSelectedForPortfolio));
    }

    [Fact]
    public async Task A_focal_point_is_recorded_on_the_image()
    {
        // The cover is the only photograph the site crops, and centred by default that
        // crop takes the head off a full-length shot.
        var ids = await UploadAsync(1);

        var (succeeded, error) = await _service.SetFocalPointAsync(_clientId, ids[0], 38, 18, null);

        Assert.True(succeeded);
        Assert.Null(error);

        var asset = await _db.MediaAssets.SingleAsync(m => m.Id == ids[0]);
        Assert.Equal(38, asset.FocalPointX);
        Assert.Equal(18, asset.FocalPointY);
    }

    [Fact]
    public async Task A_focal_point_can_be_cleared_back_to_the_default_framing()
    {
        // Null is not the same as 50/50: cleared, each place goes back to the framing
        // that suits it rather than to the middle of the photograph.
        var ids = await UploadAsync(1);
        await _service.SetFocalPointAsync(_clientId, ids[0], 38, 18, null);

        var (succeeded, _) = await _service.SetFocalPointAsync(_clientId, ids[0], null, null, null);

        Assert.True(succeeded);

        var asset = await _db.MediaAssets.SingleAsync(m => m.Id == ids[0]);
        Assert.Null(asset.FocalPointX);
        Assert.Null(asset.FocalPointY);
    }

    [Theory]
    [InlineData(-1, 50)]
    [InlineData(101, 50)]
    [InlineData(50, -1)]
    [InlineData(50, 101)]
    public async Task A_focal_point_outside_the_photograph_is_refused(int x, int y)
    {
        var ids = await UploadAsync(1);

        var (succeeded, error) = await _service.SetFocalPointAsync(_clientId, ids[0], x, y, null);

        Assert.False(succeeded);
        Assert.Contains("between 0 and 100", error);
    }

    [Fact]
    public async Task One_coordinate_on_its_own_is_refused()
    {
        // Storing half a focal point would leave the other axis silently reading as
        // centre, which is a crop nobody chose.
        var ids = await UploadAsync(1);

        var (succeeded, error) = await _service.SetFocalPointAsync(_clientId, ids[0], 40, null, null);

        Assert.False(succeeded);
        Assert.Contains("both", error);
    }

    [Fact]
    public async Task A_focal_point_on_an_unknown_image_is_refused()
    {
        var (succeeded, error) = await _service.SetFocalPointAsync(
            _clientId, Guid.CreateVersion7(), 50, 50, null);

        Assert.False(succeeded);
        Assert.Contains("could not be found", error);
    }

    [Fact]
    public async Task A_batch_sets_the_main_image_once()
    {
        // The first of the batch becomes the main image, and the rest do not each
        // overwrite it on their way through.
        var ids = await UploadAsync(3);

        await _service.SetSelectedManyAsync(_clientId, ids, null);

        var portfolio = await _db.Portfolios.SingleAsync();

        Assert.Equal(ids[0], portfolio.FeaturedMediaId);
        Assert.Equal(1, await _db.MediaAssets.CountAsync(m => m.IsFeatured));
    }

    [Fact]
    public async Task The_first_selected_image_becomes_the_main_image()
    {
        var ids = await UploadAsync(2);

        await _service.SetSelectedAsync(_clientId, ids[0], true, null);

        var asset = await _db.MediaAssets.SingleAsync(m => m.Id == ids[0]);
        var portfolio = await _db.Portfolios.SingleAsync();

        Assert.True(asset.IsFeatured);
        Assert.Equal(ids[0], portfolio.FeaturedMediaId);
    }

    /// <summary>
    /// The specification holds the featured image in two places: a flag on the asset
    /// and a foreign key on the portfolio. They must never disagree.
    /// </summary>
    [Fact]
    public async Task The_featured_flag_and_the_portfolio_reference_stay_in_step()
    {
        var ids = await UploadAsync(3);
        foreach (var id in ids) { await _service.SetSelectedAsync(_clientId, id, true, null); }

        await _service.SetFeaturedAsync(_clientId, ids[2], null);

        var flagged = await _db.MediaAssets.Where(m => m.IsFeatured).ToListAsync();
        var portfolio = await _db.Portfolios.SingleAsync();

        Assert.Single(flagged);
        Assert.Equal(ids[2], flagged[0].Id);
        Assert.Equal(ids[2], portfolio.FeaturedMediaId);
    }

    [Fact]
    public async Task Deselecting_the_main_image_promotes_another_portfolio_image()
    {
        var ids = await UploadAsync(3);
        foreach (var id in ids) { await _service.SetSelectedAsync(_clientId, id, true, null); }
        await _service.SetFeaturedAsync(_clientId, ids[0], null);

        await _service.SetSelectedAsync(_clientId, ids[0], false, null);

        var flagged = await _db.MediaAssets.Where(m => m.IsFeatured).ToListAsync();
        var portfolio = await _db.Portfolios.SingleAsync();

        Assert.Single(flagged);
        Assert.NotEqual(ids[0], flagged[0].Id);
        Assert.True(flagged[0].IsSelectedForPortfolio);
        Assert.Equal(flagged[0].Id, portfolio.FeaturedMediaId);
    }

    [Fact]
    public async Task Deleting_the_main_image_promotes_another_portfolio_image()
    {
        var ids = await UploadAsync(3);
        foreach (var id in ids) { await _service.SetSelectedAsync(_clientId, id, true, null); }
        await _service.SetFeaturedAsync(_clientId, ids[1], null);

        await _service.SoftDeleteAsync(_clientId, ids[1], null);

        var flagged = await _db.MediaAssets.Where(m => m.IsFeatured && !m.IsDeleted).ToListAsync();

        Assert.Single(flagged);
        Assert.NotEqual(ids[1], flagged[0].Id);
        Assert.Equal(flagged[0].Id, (await _db.Portfolios.SingleAsync()).FeaturedMediaId);
    }

    [Fact]
    public async Task Removing_the_last_portfolio_image_clears_the_main_image()
    {
        var ids = await UploadAsync(1);
        await _service.SetSelectedAsync(_clientId, ids[0], true, null);

        await _service.SetSelectedAsync(_clientId, ids[0], false, null);

        Assert.Empty(await _db.MediaAssets.Where(m => m.IsFeatured).ToListAsync());
        Assert.Null((await _db.Portfolios.SingleAsync()).FeaturedMediaId);
    }

    [Fact]
    public async Task An_image_not_on_the_portfolio_cannot_be_the_main_image()
    {
        var ids = await UploadAsync(2);
        await _service.SetSelectedAsync(_clientId, ids[0], true, null);

        var (succeeded, error) = await _service.SetFeaturedAsync(_clientId, ids[1], null);

        Assert.False(succeeded);
        Assert.Contains("on the portfolio", error);
    }

    /// <summary>
    /// Removal is reversible: only a Super Admin destroys media permanently, so the
    /// row and the stored file both survive.
    /// </summary>
    [Fact]
    public async Task Removing_an_image_is_a_soft_delete()
    {
        var ids = await UploadAsync(1);
        var key = (await _db.MediaAssets.SingleAsync()).StorageKey;

        await _service.SoftDeleteAsync(_clientId, ids[0], null);

        var asset = await _db.MediaAssets.SingleAsync(m => m.Id == ids[0]);
        Assert.True(asset.IsDeleted);
        Assert.NotNull(asset.DeletedAt);
        Assert.False(asset.IsSelectedForPortfolio);
        Assert.True(_storage.Contains(key));
        Assert.Empty(await _service.GetPoolAsync(_clientId));
    }

    [Fact]
    public async Task A_deleted_image_frees_a_slot_in_the_pool()
    {
        var ids = await UploadAsync(Options.MediaPoolImageLimit);
        await _service.SoftDeleteAsync(_clientId, ids[0], null);

        var outcomes = await _service.UploadImagesAsync(_clientId, [Jpeg("replacement.jpg")], null);

        Assert.True(outcomes.Single().Succeeded);
    }

    /// <summary>
    /// Another client's asset id must simply not be found, rather than acted upon
    /// (specification section 35).
    /// </summary>
    [Fact]
    public async Task An_asset_belonging_to_another_client_cannot_be_touched()
    {
        var ids = await UploadAsync(1);
        var otherClient = Guid.CreateVersion7();

        var select = await _service.SetSelectedAsync(otherClient, ids[0], true, null);
        var deleted = await _service.SoftDeleteAsync(otherClient, ids[0], null);

        Assert.False(select.Succeeded);
        Assert.False(deleted);
        Assert.False((await _db.MediaAssets.SingleAsync()).IsDeleted);
    }

    [Fact]
    public async Task Reordering_ignores_ids_from_another_client()
    {
        var ids = await UploadAsync(3);

        await _service.ReorderAsync(_clientId, [ids[2], Guid.CreateVersion7(), ids[0], ids[1]], null);

        var ordered = await _service.GetPoolAsync(_clientId);
        Assert.Equal([ids[2], ids[0], ids[1]], ordered.Select(a => a.Id));
    }

    [Fact]
    public async Task A_self_tape_can_be_uploaded_replaced_and_removed()
    {
        var first = await _service.UploadSelfTapeAsync(_clientId, Raw("take1.mp4", "video/mp4", 2048), null);
        Assert.True(first.Succeeded);
        Assert.Equal("take1.mp4", (await _service.GetSelfTapeAsync(_clientId))!.OriginalFilename);

        // Replacing keeps exactly one self-tape (specification section 14).
        var second = await _service.UploadSelfTapeAsync(_clientId, Raw("take2.mp4", "video/mp4", 2048), null);
        Assert.True(second.Succeeded);

        var current = await _service.GetSelfTapeAsync(_clientId);
        Assert.Equal("take2.mp4", current!.OriginalFilename);
        Assert.Equal(1, await _db.MediaAssets.CountAsync(m => m.MediaType == MediaType.SelfTape && !m.IsDeleted));

        Assert.True(await _service.RemoveSelfTapeAsync(_clientId, null));
        Assert.Null(await _service.GetSelfTapeAsync(_clientId));
    }

    [Fact]
    public async Task An_oversized_or_unsupported_self_tape_is_rejected()
    {
        var tooBig = await _service.UploadSelfTapeAsync(
            _clientId, Raw("big.mp4", "video/mp4", (int)Options.MaxVideoBytes + 1), null);
        var wrongFormat = await _service.UploadSelfTapeAsync(
            _clientId, Raw("clip.avi", "video/x-msvideo", 1024), null);

        Assert.False(tooBig.Succeeded);
        Assert.False(wrongFormat.Succeeded);
        Assert.Null(await _service.GetSelfTapeAsync(_clientId));
    }

    /// <summary>A self-tape is not a photograph and must not consume a pool slot.</summary>
    [Fact]
    public async Task A_self_tape_does_not_count_against_the_image_pool()
    {
        await UploadAsync(Options.MediaPoolImageLimit);

        var outcome = await _service.UploadSelfTapeAsync(_clientId, Raw("t.mp4", "video/mp4", 1024), null);

        Assert.True(outcome.Succeeded);
        Assert.DoesNotContain(await _service.GetPoolAsync(_clientId), a => a.MediaType == MediaType.SelfTape);
    }
}

/// <summary>Storage backed by a dictionary, so tests never touch the filesystem.</summary>
/// <summary>Reads headers like any image, then refuses to decode — as a truncated or
/// out-of-memory decode does.</summary>
internal class RefusesToDecode : IImageProcessor
{
    private readonly ImageProcessor _real = new(NullLogger<ImageProcessor>.Instance);

    public ImageDetails? Inspect(Stream content) => _real.Inspect(content);

    public ProcessedImage Process(Stream content) => new([], null);
}

internal class InMemoryStorage : IMediaStorageService
{
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);

    public bool Contains(string key) => _files.ContainsKey(key);

    public IReadOnlyCollection<string> Keys => _files.Keys;

    public async Task<StoredMedia> UploadAsync(
        Stream content, string storageKey, string contentType, CancellationToken cancellationToken = default)
    {
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);
        _files[storageKey] = buffer.ToArray();

        return new StoredMedia(storageKey, buffer.Length, contentType);
    }

    public Task<Stream?> GetAsync(string storageKey, CancellationToken cancellationToken = default) =>
        Task.FromResult<Stream?>(_files.TryGetValue(storageKey, out var bytes) ? new MemoryStream(bytes) : null);

    public Task DeleteAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        _files.Remove(storageKey);
        return Task.CompletedTask;
    }

    public Task<bool> ExistsAsync(string storageKey, CancellationToken cancellationToken = default) =>
        Task.FromResult(_files.ContainsKey(storageKey));
}
