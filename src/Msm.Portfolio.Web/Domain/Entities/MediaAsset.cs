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
    /// Soft delete. Removal is reversible and leaves the audit trail intact;
    /// only a Super Admin may destroy media permanently.
    /// </summary>
    public bool IsDeleted { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }
}
