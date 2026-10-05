namespace Msm.Portfolio.Web.Domain.Enums;

/// <summary>
/// Portfolio lifecycle, per specification section 27. Deliberately a lifecycle
/// rather than a single boolean so that "prepared but not sold" and "sold but
/// unpublished" remain distinguishable.
/// </summary>
public enum PortfolioStatus
{
    AwaitingClientInformation = 0,
    ReadyForRetoucher = 1,
    Retouching = 2,
    ReadyForReview = 3,
    InViewing = 4,
    AwaitingPurchase = 5,
    Purchased = 6,
    Published = 7,
    PaymentWarning = 8,
    Unpublished = 9,
    NoSale = 10,
    Archived = 11
}

public static class PortfolioStatusExtensions
{
    /// <summary>
    /// A human-readable label with spaces, for anywhere this is shown to staff — the
    /// enum's own name has none, which read as one run-together word on the Clients
    /// list and elsewhere.
    /// </summary>
    public static string Label(this PortfolioStatus status) => status switch
    {
        PortfolioStatus.AwaitingClientInformation => "Awaiting client information",
        PortfolioStatus.ReadyForRetoucher => "Ready for retoucher",
        PortfolioStatus.Retouching => "Retouching",
        PortfolioStatus.ReadyForReview => "Ready for review",
        PortfolioStatus.InViewing => "In viewing",
        PortfolioStatus.AwaitingPurchase => "Awaiting purchase",
        PortfolioStatus.Purchased => "Purchased",
        PortfolioStatus.Published => "Published",
        PortfolioStatus.PaymentWarning => "Payment warning",
        PortfolioStatus.Unpublished => "Unpublished",
        PortfolioStatus.NoSale => "No sale",
        PortfolioStatus.Archived => "Archived",
        _ => status.ToString()
    };
}
