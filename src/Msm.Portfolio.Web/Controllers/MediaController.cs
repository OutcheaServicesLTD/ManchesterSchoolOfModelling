using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Msm.Portfolio.Web.Authorization;
using Msm.Portfolio.Web.Data;
using Msm.Portfolio.Web.Domain.Entities;
using Msm.Portfolio.Web.Domain.Enums;
using Msm.Portfolio.Web.Services;
using Msm.Portfolio.Web.Storage;

namespace Msm.Portfolio.Web.Controllers;

/// <summary>
/// Streams media out of storage.
/// </summary>
/// <remarks>
/// Media never sits under wwwroot. A client's pool holds up to 60 images of which at
/// most 30 are ever public, so serving files straight off disk would expose the
/// unpublished remainder to anyone who guessed a path. Every request is authorised
/// here instead.
/// </remarks>
[Route("media")]
[AllowAnonymous]
public class MediaController(
    ApplicationDbContext db,
    IMediaStorageService storage,
    IMediaService media,
    IImageProcessor images,
    UserManager<ApplicationUser> userManager) : Controller
{
    [HttpGet("{assetId:guid}/{variant?}")]
    public async Task<IActionResult> Get(
        Guid assetId,
        string? variant = null,
        CancellationToken cancellationToken = default)
    {
        var asset = await db.MediaAssets
            .Include(m => m.Client)
            .ThenInclude(c => c.Portfolio)
            .FirstOrDefaultAsync(m => m.Id == assetId && !m.IsDeleted, cancellationToken);

        if (asset is null)
        {
            return NotFound();
        }

        if (!await IsAllowedAsync(asset))
        {
            // Deliberately NotFound rather than Forbid: a 403 would confirm that this
            // asset id exists, which is itself information about a private library.
            return NotFound();
        }

        var requested = ParseVariant(variant);

        // Staff may fetch the archived original; everyone else is served the large
        // rendition instead.
        //
        // A retoucher judging whether a frame is sharp enough to publish cannot do it
        // from a re-encoded copy at 88% quality — the very thing they are looking for is
        // what the compression removed. The public gets the rendition: it is a fraction
        // of the bytes, and a full-resolution camera file is the studio's asset, not
        // something to hand out with the web page.
        var served = requested == MediaVariant.Original
                     && asset.MediaType == MediaType.Image
                     && !IsStaff()
            ? MediaVariant.Large
            : requested;

        var key = asset.MediaType == MediaType.SelfTape
            ? asset.StorageKey
            : MediaStorageKeys.ForVariant(asset.StorageKey, served);

        var stream = await storage.GetAsync(key, cancellationToken);

        // A missing rendition is not a missing photograph. The original is archived
        // exactly as uploaded, so anything that interrupted the renditions being made —
        // a decode that ran out of memory, a deploy that landed mid-batch — leaves a
        // library that can be rebuilt rather than one that has to be uploaded again.
        //
        // Done here, on the first request for the image, so a library of broken tiles
        // repairs itself the next time somebody opens the page. Rebuilding is bounded to
        // two at a time inside the service, so a grid of sixty cannot start sixty decodes.
        // Rebuilt when the file is missing, and also when it was made under older, smaller
        // targets. A raised target that only applied to the next upload would leave every
        // portfolio already in the system looking exactly as it did — which is the case
        // the complaint came from.
        //
        // Only for the large rendition, though. A library page asks for sixty thumbnails
        // at once, and rebuilding on the strength of the version alone turned one page
        // view into sixty decodes queueing behind each other — competing for memory with
        // whatever the retoucher was uploading at the time, and taking the process down
        // with it. Only the large rendition changed size in a way anybody can see, so
        // only the large rendition is worth redoing for it; a thumbnail from the old
        // targets is the same thumbnail.
        var stale = asset.RenditionVersion < ImageProcessor.RenditionVersion
                    && served == MediaVariant.Large;

        if ((stream is null || stale) && asset.MediaType == MediaType.Image)
        {
            // Closed before the rebuild, not after: this handle is on the very file about
            // to be overwritten, and leaving it open both leaks it and risks the write
            // failing on a platform that locks open files.
            if (stream is not null)
            {
                await stream.DisposeAsync();
                stream = null;
            }

            if (await media.RebuildVariantsAsync(asset.Id, cancellationToken))
            {
                stream = await storage.GetAsync(key, cancellationToken);
            }

            // A rebuild that could not run leaves the original file in place, so the
            // older rendition is still worth serving — better a soft photograph than none.
            stream ??= await storage.GetAsync(key, cancellationToken);
        }

        if (stream is null)
        {
            return NotFound();
        }

        var contentType = asset.MediaType == MediaType.SelfTape || served == MediaVariant.Original
            ? asset.MimeType
            : "image/jpeg";

        // Immutable: an asset id always refers to the same photograph, and replacing an
        // image creates a new asset rather than overwriting one.
        //
        // An original is never cached publicly, even on a published portfolio. It is only
        // ever served to staff, and a shared cache holding the full-resolution file could
        // hand it to someone the check above would have refused.
        Response.Headers.CacheControl = asset.IsSelectedForPortfolio && served != MediaVariant.Original
            ? "public, max-age=31536000, immutable"
            : "private, max-age=3600";

        // A rendition, offered as WebP to a browser that says it can use one
        // (specification version 2, item 1). Re-encoded from the JPEG rendition rather
        // than stored as a fourth upload-time variant, so this is the only place that
        // decision lives; a browser that never sends the header is untouched, and one
        // that does gets a materially smaller download for the same photograph.
        if (contentType == "image/jpeg" && AcceptsWebp())
        {
            // Caches must not hand a WebP response to a browser that cannot decode it,
            // or the other way round.
            Response.Headers.Vary = "Accept";

            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, cancellationToken);

            if (images.ToWebp(buffer.ToArray()) is { } webp)
            {
                return File(webp, "image/webp", enableRangeProcessing: true);
            }

            buffer.Position = 0;
            return File(buffer.ToArray(), contentType, enableRangeProcessing: true);
        }

        // Range support lets a self-tape be scrubbed rather than only played from the start.
        return File(stream, contentType, enableRangeProcessing: true);
    }

    /// <summary>
    /// Whether this browser said it can decode WebP. Every browser that supports it has
    /// sent <c>image/webp</c> in Accept since 2020; one that has not is treated as
    /// unable to, which only ever costs it the JPEG it would have received anyway.
    /// </summary>
    private bool AcceptsWebp() =>
        Request.Headers.Accept.Any(value =>
            value is not null && value.Contains("image/webp", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Who may see this file. Staff see everything, a client sees their own library,
    /// and the public sees only images the client has put on a published portfolio.
    /// </summary>
    private bool IsStaff() =>
        User.Identity?.IsAuthenticated == true
        && (User.IsInRole(Roles.SuperAdmin) || User.IsInRole(Roles.Admin) || User.IsInRole(Roles.Retoucher));

    private async Task<bool> IsAllowedAsync(MediaAsset asset)
    {
        if (User.Identity?.IsAuthenticated == true)
        {
            if (IsStaff())
            {
                return true;
            }

            var userId = userManager.GetUserId(User);
            if (Guid.TryParse(userId, out var id) && asset.Client.ApplicationUserId == id)
            {
                return true;
            }
        }

        // Both conditions matter: an image selected for a portfolio that has not been
        // published is still private, and an unselected image on a published portfolio
        // is part of the private pool.
        return asset.IsSelectedForPortfolio
               && asset.Client.Portfolio is { IsPublished: true };
    }

    private static MediaVariant ParseVariant(string? variant) =>
        Enum.TryParse<MediaVariant>(variant, ignoreCase: true, out var parsed)
            ? parsed
            : MediaVariant.Medium;
}
