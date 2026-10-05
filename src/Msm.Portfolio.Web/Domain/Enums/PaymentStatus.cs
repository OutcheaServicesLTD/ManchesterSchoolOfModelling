namespace Msm.Portfolio.Web.Domain.Enums;

/// <summary>
/// Payment state, per specification section 29. Held separately from portfolio
/// state: a failed payment produces Payment=Failed and Portfolio=PaymentWarning
/// rather than one field carrying two different concepts.
/// </summary>
public enum PaymentStatus
{
    Pending = 0,
    CheckoutStarted = 1,
    Authorised = 2,
    Submitted = 3,
    Confirmed = 4,
    Failed = 5,
    Cancelled = 6,
    Reviewed = 7
}
