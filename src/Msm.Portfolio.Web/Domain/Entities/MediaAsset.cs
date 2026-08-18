using Msm.Portfolio.Web.Domain.Enums;

namespace Msm.Portfolio.Web.Domain.Entities;

/// <summary>
/// One uploaded image or self-tape in a client's private media pool
/// (specification sections 12, 13, 14 and 26).
/// </summary>
/// <remarks>
/// The pool is deliberately larger than the public portfolio: staff work from up to
/// 60 assets and publish at most 30, so <see cref="IsSelectedForPortfolio"/> is what
/// separates the private pool from what an agency sees.
/// </remarks>
public class MediaAsset
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ClientId { get; set; }

    public ClientProfile Client { get; set; } = null!;

    /// <summary>
    /// Provider-agnostic key for the stored original. Resolved through
    /// IMediaStorageService, so the storage provider stays a deployment decision
    /// (specification section 33).
    /// </summary>
    public string StorageKey { get; set; } = string.Empty;

    public string OriginalFilename { get; set; } = string.Empty;

    public string MimeType { get; set; } = string.Empty;

    public long FileSize { get; set; }

    public int? Width { get; set; }

    public int? Height { get; set; }

    /// <summary>
    /// Recorded at upload so the gallery can lay out portrait and landscape work
    /// without destructively cropping originals (specification section 13).
    /// </summary>
    public MediaOrientation Orientation { get; set; } = MediaOrientation.Unknown;

    public MediaType MediaType { get; set; } = MediaType.Image;

    public Guid? UploadedByUserId { get; set; }

    public ApplicationUser? UploadedByUser { get; set; }

    public DateTimeOffset UploadedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>True when this asset appears on the public portfolio.</summary>
    public bool IsSelectedForPortfolio { get; set; }

    /// <summary>
    /// At most one asset per client carries this flag. Admin can override the
    /// retoucher's or client's choice (specification section 12).
    /// </summary>
    public bool IsFeatured { get; set; }

    public int DisplayOrder { get; set; }

    /// <summary>
    /// Where the important part of the picture is, across and down, as a percentage of
    /// the image; null when nobody has said.
    /// </summary>
    /// <remarks>
    /// Only the cover photograph is ever cropped — the hero band at the top of a
    /// portfolio and the card on the Model Board both fill a fixed shape, while the
    /// gallery below shows every photograph whole. A crop centred by default cuts the
    /// head off a full-length shot and takes the shoulder out of a close one, so staff
    /// need a way to say which part must survive.
    /// <para>
    /// Stored on the asset rather than on the portfolio because it describes the
    /// photograph itself, and so it is still right if a different image is made the
    /// cover later and this one is made the cover again.
    /// </para>
    /// <para>
    /// Null is not the same as 50/50: left unset, each place keeps the default that
    /// suits it, which for the hero band is above centre because faces usually are.
    /// </para>
    /// </remarks>
    public int? FocalPointX { get; set; }

    public int? FocalPointY { get; set; }

    /// <summary>
    /// What could be measured about the photograph at upload, as percentages; null for
    /// anything uploaded before measuring existed, or that could not be read.
    /// </summary>
    /// <remarks>
    /// These exist for one purpose: offering a retoucher a starting selection out of a
    /// pool of sixty, so the obviously soft and the obviously blown frames are not the
    /// ones they have to find by hand. They describe the picture only — how much fine
    /// detail it holds, how bright it is, how much tonal range it uses, and how much of
    /// it has been lost to pure black or pure white. Nothing here describes the person in
    /// the frame, and nothing here should: the suggestion is a suggestion, and a person
    /// decides.
    /// </remarks>
    public int? Sharpness { get; set; }

    public int? Exposure { get; set; }

    public int? Contrast { get; set; }

    public int? Clipping { get; set; }

    public bool HasBeenMeasured => Sharpness is not null;

    /// <summary>
    /// Soft delete. Removal is reversible and leaves the audit trail intact;
    /// only a Super Admin may destroy media permanently.
    /// </summary>
    public bool IsDeleted { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }
}
