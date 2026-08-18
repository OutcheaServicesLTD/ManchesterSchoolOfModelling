using Msm.Portfolio.Web.Domain.Enums;

namespace Msm.Portfolio.Web.ViewModels;

/// <summary>One image in the library grid (specification section 12).</summary>
public class MediaAssetViewModel
{
    public Guid Id { get; set; }

    public string Filename { get; set; } = string.Empty;

    public MediaOrientation Orientation { get; set; }

    public bool IsSelected { get; set; }

    public bool IsFeatured { get; set; }

    public int? Width { get; set; }

    public int? Height { get; set; }

    /// <summary>Where the important part of the picture is, if anyone has said.</summary>
    public int? FocalPointX { get; set; }

    public int? FocalPointY { get; set; }

    /// <summary>
    /// Aspect ratio for the grid tile, so the browser reserves the right space before
    /// the image loads and the page does not jump as thumbnails arrive.
    /// </summary>
    public string AspectRatio =>
        Width is > 0 && Height is > 0 ? $"{Width} / {Height}" : "3 / 4";

    public bool HasFocalPoint => FocalPointX is not null && FocalPointY is not null;

    /// <summary>
    /// True when this photograph is part of the suggested starting selection. Nothing is
    /// selected by it: the page ticks the box and a person decides.
    /// </summary>
    public bool IsSuggested { get; set; }
}

/// <summary>
/// The cover photograph and the crop staff have chosen for it.
/// </summary>
/// <remarks>
/// The cover is the only photograph that is ever cropped: it fills the band across the
/// top of the portfolio and the card on the Model Board, both fixed shapes. Everything
/// else in the gallery is shown whole. Centred by default, that crop takes the head off
/// a full-length shot, so staff can say which part has to survive.
/// </remarks>
public class CoverFocusViewModel
{
    public Guid AssetId { get; set; }

    public string Filename { get; set; } = string.Empty;

    /// <summary>Where the form posts to, which differs between the staff areas.</summary>
    public string PostUrl { get; set; } = string.Empty;

    public int? X { get; set; }

    public int? Y { get; set; }

    public bool IsSet => X is not null && Y is not null;

    /// <summary>
    /// What the sliders start on. Not centred down the image: the stylesheet's own
    /// default sits above the middle, because a face usually does, and starting anywhere
    /// else would move the crop the moment the panel is opened.
    /// </summary>
    public int StartX => X ?? 50;

    public int StartY => Y ?? 22;
}

public class MediaLibraryViewModel
{
    public List<MediaAssetViewModel> Assets { get; set; } = [];

    public int PoolLimit { get; set; }

    public int PortfolioLimit { get; set; }

    public long MaxImageBytes { get; set; }

    public string[] AllowedContentTypes { get; set; } = [];

    public int SelectedCount => Assets.Count(a => a.IsSelected);

    public bool PoolIsFull => Assets.Count >= PoolLimit;

    public bool PortfolioIsFull => SelectedCount >= PortfolioLimit;

    public string AcceptAttribute => string.Join(",", AllowedContentTypes);

    public long MaxImageMegabytes => MaxImageBytes / (1024 * 1024);
}

/// <summary>The client's self-tape (specification section 14).</summary>
public class SelfTapeViewModel
{
    public Guid? AssetId { get; set; }

    public string? Filename { get; set; }

    public long MaxBytes { get; set; }

    public string[] AllowedContentTypes { get; set; } = [];

    public bool HasSelfTape => AssetId is not null;

    public string AcceptAttribute => string.Join(",", AllowedContentTypes);

    public long MaxMegabytes => MaxBytes / (1024 * 1024);
}
