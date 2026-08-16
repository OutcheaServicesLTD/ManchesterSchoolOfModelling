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

    /// <summary>
    /// Aspect ratio for the grid tile, so the browser reserves the right space before
    /// the image loads and the page does not jump as thumbnails arrive.
    /// </summary>
    public string AspectRatio =>
        Width is > 0 && Height is > 0 ? $"{Width} / {Height}" : "3 / 4";
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
