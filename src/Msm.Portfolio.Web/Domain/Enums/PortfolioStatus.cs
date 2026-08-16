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
