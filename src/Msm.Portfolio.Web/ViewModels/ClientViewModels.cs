using System.ComponentModel.DataAnnotations;
using Msm.Portfolio.Web.Configuration;
using Msm.Portfolio.Web.Domain.Enums;

namespace Msm.Portfolio.Web.ViewModels;

/// <summary>The client's dashboard summary (specification section 17).</summary>
public class ClientDashboardViewModel
{
    public string Name { get; set; } = string.Empty;

    public PortfolioStatus PortfolioStatus { get; set; }

    public bool IsPublished { get; set; }

    /// <summary>Full public URL, shown with a copy button once the portfolio is live.</summary>
    public string? PublicUrl { get; set; }

    public int PortfolioImageCount { get; set; }

    public int PortfolioImageLimit { get; set; }

    public int MediaPoolCount { get; set; }

    public int MediaPoolLimit { get; set; }

    /// <summary>Rough completeness of the profile, used to prompt the client to finish it.</summary>
    public int ProfileCompletionPercent { get; set; }

    public bool GuardianApprovalPending { get; set; }

    /// <summary>
    /// Shown to the client and to staff, never on the public portfolio
    /// (specification section 23).
    /// </summary>
    public Services.MaintenanceWarning? MaintenanceWarning { get; set; }

    /// <summary>
    /// When the purchased year ends. Null before anything is bought, and on portfolios
    /// sold before the year had a meaning.
    /// </summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>
    /// True when an administrator is looking at this dashboard from the client record
    /// rather than the model looking at their own.
    /// </summary>
    /// <remarks>
    /// Changes nothing about what is shown — that is the point, and a preview that showed
    /// something different would be worthless. It only stands down the controls that
    /// belong to the model: their own profile form is theirs to open, and staff edit the
    /// same details on the record behind the preview.
    /// </remarks>
    public bool IsPreview { get; set; }

    // ── Portfolio-maintenance subscription (specification version 2, item 3) ───────
    // Optional, and started by the client themselves — quite separate from the £99
    // purchase above, which is why it carries its own null/available checks rather
    // than folding into ExpiresAt or MaintenanceWarning.

    /// <summary>True once Stripe is configured and a client can actually be sent there.</summary>
    public bool SubscriptionAvailable { get; set; }

    /// <summary>Null when this client has never started a subscription.</summary>
    public Domain.Enums.MaintenanceSubscriptionStatus? SubscriptionStatus { get; set; }

    public decimal SubscriptionPrice { get; set; }

    public string SubscriptionCurrency { get; set; } = "GBP";

    public DateTimeOffset? SubscriptionNextPaymentDate { get; set; }

    public bool HasSubscription => SubscriptionStatus is not (null or Domain.Enums.MaintenanceSubscriptionStatus.NotStarted);

    public bool SubscriptionCanBeManaged =>
        SubscriptionStatus is Domain.Enums.MaintenanceSubscriptionStatus.Active
            or Domain.Enums.MaintenanceSubscriptionStatus.PaymentIssue
            or Domain.Enums.MaintenanceSubscriptionStatus.GracePeriodExpired;

    public string SubscriptionStatusLabel => SubscriptionStatus switch
    {
        Domain.Enums.MaintenanceSubscriptionStatus.Active => "Active",
        Domain.Enums.MaintenanceSubscriptionStatus.PaymentIssue => "Payment problem",
        Domain.Enums.MaintenanceSubscriptionStatus.GracePeriodExpired => "Payment problem",
        Domain.Enums.MaintenanceSubscriptionStatus.Cancelled => "Ended",
        Domain.Enums.MaintenanceSubscriptionStatus.Ended => "Ended",
        _ => "Not started"
    };

    /// <summary>Days left of the purchased year, or null when there is no term.</summary>
    public int? DaysOfTermRemaining =>
        ExpiresAt is { } expires ? (int)Math.Ceiling((expires - DateTimeOffset.UtcNow).TotalDays) : null;

    /// <summary>
    /// Whether to say anything about it. A year away is not news; a month away is, and
    /// is enough notice to do something about it.
    /// </summary>
    public bool TermIsEndingSoon => DaysOfTermRemaining is > 0 and <= 30;

    public string StatusDescription => PortfolioStatus switch
    {
        PortfolioStatus.AwaitingClientInformation => "We are waiting for your details.",
        PortfolioStatus.ReadyForRetoucher => "Your photographs are queued with our team.",
        PortfolioStatus.Retouching => "Our team is preparing your portfolio.",
        PortfolioStatus.ReadyForReview => "Your portfolio is with our team for review.",
        PortfolioStatus.InViewing => "Your portfolio is ready to view.",
        PortfolioStatus.AwaitingPurchase => "Your portfolio is ready to view.",
        PortfolioStatus.Purchased => "Thank you. We are publishing your portfolio.",
        PortfolioStatus.Published => "Your portfolio is live.",
        PortfolioStatus.PaymentWarning => "Your portfolio is live, but there is a problem with your payment.",
        PortfolioStatus.Unpublished => "Your portfolio is not currently public.",
        PortfolioStatus.NoSale => "Your portfolio is not currently public.",
        PortfolioStatus.Archived => "Your portfolio has been archived.",
        _ => string.Empty
    };
}

/// <summary>
/// The client editing their own profile (specification section 17). Once the portfolio
/// is live these changes reach the public page immediately, with no approval step.
/// </summary>
public class ClientProfileViewModel : IValidatableObject
{
    [Required(ErrorMessage = "Please enter your first name.")]
    [StringLength(100)]
    [Display(Name = "First name")]
    public string FirstName { get; set; } = string.Empty;

    [Required(ErrorMessage = "Please enter your last name.")]
    [StringLength(100)]
    [Display(Name = "Last name")]
    public string LastName { get; set; } = string.Empty;

    [StringLength(150)]
    [Display(Name = "Model name (if different)")]
    public string? DisplayName { get; set; }

    [Required(ErrorMessage = "Please enter your date of birth.")]
    [DataType(DataType.Date)]
    [Display(Name = "Date of birth")]
    public DateOnly? DateOfBirth { get; set; }

    [StringLength(200)]
    [Display(Name = "Location")]
    public string? Location { get; set; }

    [Display(Name = "Profile type")]
    public ModelProfileType ModelProfileType { get; set; }

    [StringLength(50)]
    [Display(Name = "Hair colour")]
    public string? HairColour { get; set; }

    [StringLength(50)]
    [Display(Name = "Eye colour")]
    public string? EyeColour { get; set; }

    [StringLength(4000)]
    [Display(Name = "About me")]
    public string? Biography { get; set; }

    [Url(ErrorMessage = "Please enter a valid link.")]
    [StringLength(500)]
    [Display(Name = "Instagram")]
    public string? InstagramUrl { get; set; }

    [Url(ErrorMessage = "Please enter a valid link.")]
    [StringLength(500)]
    [Display(Name = "TikTok")]
    public string? TikTokUrl { get; set; }

    public List<MeasurementInputModel> Measurements { get; set; } = [];

    public IReadOnlyList<MeasurementFieldDefinition> Template { get; set; } = [];

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        if (DateOfBirth is { } dob && dob > today)
        {
            yield return new ValidationResult(
                "Date of birth cannot be in the future.", [nameof(DateOfBirth)]);
        }

        foreach (var field in Template.Where(f => f.Required))
        {
            var entered = Measurements.FirstOrDefault(m => m.Key == field.Key);

            if (entered is null || string.IsNullOrWhiteSpace(entered.Value))
            {
                yield return new ValidationResult(
                    $"Please enter your {field.Label.ToLowerInvariant()}.", [nameof(Measurements)]);
            }
        }
    }
}
