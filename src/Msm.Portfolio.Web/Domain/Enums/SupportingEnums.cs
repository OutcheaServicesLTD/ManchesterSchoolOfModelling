namespace Msm.Portfolio.Web.Domain.Enums;

/// <summary>
/// Selects which measurement template the onboarding form presents (specification section 9).
/// Templates are data-driven, so adding a profile type does not require a schema change.
/// </summary>
public enum ModelProfileType
{
    Unspecified = 0,
    Female = 1,
    Male = 2
}

/// <summary>Unit a measurement value was captured in (specification section 9).</summary>
public enum MeasurementUnit
{
    None = 0,
    Centimetres = 1,
    Inches = 2,
    UkDressSize = 3,
    UkShoeSize = 4,
    UkSuitSize = 5,
    Text = 6
}

/// <summary>Guardian approval state for under-18 clients (specification section 11).</summary>
public enum GuardianConsentStatus
{
    NotRequired = 0,
    Pending = 1,
    Approved = 2,
    Declined = 3,
    Expired = 4
}

/// <summary>Distinguishes portfolio photography from the model self-tape (specification section 14).</summary>
public enum MediaType
{
    Image = 0,
    SelfTape = 1
}

/// <summary>
/// Stored at upload time so galleries can lay out portrait and landscape work
/// without re-reading the file (specification section 13).
/// </summary>
public enum MediaOrientation
{
    Unknown = 0,
    Portrait = 1,
    Landscape = 2,
    Square = 3
}

/// <summary>Retoucher queue state, matching the dashboard tabs in specification section 6.</summary>
public enum RetoucherAssignmentStatus
{
    Waiting = 0,
    InProgress = 1,
    ReadyForReview = 2,
    Completed = 3
}

/// <summary>Order lifecycle for the one-off programme purchase (specification section 19).</summary>
public enum OrderStatus
{
    Draft = 0,
    CheckoutStarted = 1,
    AwaitingPayment = 2,
    Confirmed = 3,
    Failed = 4,
    Cancelled = 5,
    NoSale = 6
}

/// <summary>Whether a product is charged once or on a recurring cycle (specification section 26).</summary>
public enum BillingType
{
    OneOff = 0,
    Recurring = 1
}

/// <summary>Recurring collection cadence for subscription products.</summary>
public enum BillingInterval
{
    None = 0,
    Weekly = 1,
    Monthly = 2,
    Yearly = 3
}

/// <summary>
/// Maintenance subscription state, including the seven-day grace period that
/// keeps a portfolio public while a failed payment is resolved (specification section 23).
/// </summary>
public enum MaintenanceSubscriptionStatus
{
    NotStarted = 0,
    Active = 1,
    PaymentIssue = 2,
    GracePeriodExpired = 3,
    Cancelled = 4,
    Ended = 5
}

/// <summary>Idempotency state for an inbound provider webhook (specification section 44).</summary>
public enum WebhookProcessingStatus
{
    Received = 0,
    Processed = 1,
    Failed = 2,
    Ignored = 3
}

/// <summary>Outcome of pushing portfolio state to the CRM (specification section 45).</summary>
public enum CrmSyncStatus
{
    NotSynced = 0,
    Pending = 1,
    Synced = 2,
    Failed = 3
}
