using Msm.Portfolio.Web.Domain.Enums;
using Msm.Portfolio.Web.Services;

namespace Msm.Portfolio.Web.ViewModels;

/// <summary>The retoucher dashboard (specification section 6).</summary>
public class RetoucherQueueViewModel
{
    public RetoucherQueueTab Tab { get; set; }

    public List<QueueItem> Items { get; set; } = [];

    public QueueCounts Counts { get; set; } = new(0, 0, 0, 0);

    public static string Label(RetoucherQueueTab tab) => tab switch
    {
        RetoucherQueueTab.Waiting => "Waiting",
        RetoucherQueueTab.InProgress => "In progress",
        RetoucherQueueTab.ReadyForReview => "Ready for review",
        RetoucherQueueTab.Completed => "Completed",
        _ => tab.ToString()
    };

    public int CountFor(RetoucherQueueTab tab) => tab switch
    {
        RetoucherQueueTab.Waiting => Counts.Waiting,
        RetoucherQueueTab.InProgress => Counts.InProgress,
        RetoucherQueueTab.ReadyForReview => Counts.ReadyForReview,
        RetoucherQueueTab.Completed => Counts.Completed,
        _ => 0
    };
}

/// <summary>The workspace for one client (specification section 6).</summary>
public class RetoucherWorkspaceViewModel
{
    public Guid ClientId { get; set; }

    public string ClientName { get; set; } = string.Empty;

    public PortfolioStatus Status { get; set; }

    public RetoucherAssignmentStatus? AssignmentStatus { get; set; }

    public List<MediaAssetViewModel> Assets { get; set; } = [];

    public int PoolLimit { get; set; }

    public int PortfolioLimit { get; set; }

    public long MaxImageBytes { get; set; }

    public string[] AllowedContentTypes { get; set; } = [];

    /// <summary>
    /// Shown so the retoucher knows the portfolio cannot complete yet. It does not stop
    /// them preparing it (specification section 11).
    /// </summary>
    public bool GuardianApprovalPending { get; set; }

    public List<MediaAssetViewModel> Selected => [.. Assets.Where(a => a.IsSelected)];

    public List<MediaAssetViewModel> Unselected => [.. Assets.Where(a => !a.IsSelected)];

    public bool PoolIsFull => Assets.Count >= PoolLimit;

    public bool PortfolioIsFull => Selected.Count >= PortfolioLimit;

    public bool HasFeatured => Assets.Any(a => a.IsFeatured);

    public bool CanSubmit => Selected.Count > 0 && HasFeatured;

    /// <summary>
    /// InViewing is included because submitting now carries a portfolio straight there
    /// (specification section 27, version 2) — a retoucher reopening it should still be
    /// told it has already gone in, not just while it briefly sat at ReadyForReview.
    /// </summary>
    public bool AlreadySubmitted => Status is PortfolioStatus.ReadyForReview or PortfolioStatus.InViewing;

    public string AcceptAttribute => string.Join(",", AllowedContentTypes);

    public long MaxImageMegabytes => MaxImageBytes / (1024 * 1024);
}
