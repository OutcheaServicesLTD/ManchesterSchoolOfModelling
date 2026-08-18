using Msm.Portfolio.Web.Domain.Enums;

namespace Msm.Portfolio.Web.Domain.Entities;

/// <summary>
/// The model themselves (specification sections 8, 10 and 26).
/// </summary>
public class ClientProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ApplicationUserId { get; set; }

    public ApplicationUser ApplicationUser { get; set; } = null!;

    /// <summary>
    /// Permanent link to the GoHighLevel contact (specification section 25). The CRM
    /// identifier is authoritative; email and telephone must never be used as the key
    /// because either can change without the contact changing.
    /// </summary>
    public string? GhlContactId { get; set; }

    public string FirstName { get; set; } = string.Empty;

    public string LastName { get; set; } = string.Empty;

    /// <summary>Public-facing model name, where it differs from the legal name.</summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// Stored instead of an age so the displayed age stays correct over time and the
    /// under-18 guardian rule can be re-evaluated at any point (specification section 8).
    /// </summary>
    public DateOnly? DateOfBirth { get; set; }

    public string? Location { get; set; }

    public ModelProfileType ModelProfileType { get; set; } = ModelProfileType.Unspecified;

    public string? Biography { get; set; }

    public string? HairColour { get; set; }

    public string? EyeColour { get; set; }

    /// <summary>Approved social links. Never used as a public contact route for the model.</summary>
    public string? InstagramUrl { get; set; }

    public string? TikTokUrl { get; set; }

    public ClientAccountStatus AccountStatus { get; set; } = ClientAccountStatus.Invited;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public Portfolio? Portfolio { get; set; }

    public GuardianConsent? GuardianConsent { get; set; }

    public ICollection<ModelMeasurement> Measurements { get; set; } = new List<ModelMeasurement>();

    public ICollection<MediaAsset> MediaAssets { get; set; } = new List<MediaAsset>();

    public ICollection<RetoucherAssignment> RetoucherAssignments { get; set; } = new List<RetoucherAssignment>();

    public ICollection<Order> Orders { get; set; } = new List<Order>();

    public string FullName => $"{FirstName} {LastName}".Trim();

    public string PublicName => string.IsNullOrWhiteSpace(DisplayName) ? FullName : DisplayName;

    /// <summary>
    /// Age derived from date of birth, so no stored age can drift out of date.
    /// Returns null when no date of birth has been captured yet.
    /// </summary>
    public int? AgeOn(DateOnly today)
    {
        if (DateOfBirth is not { } dob)
        {
            return null;
        }

        var age = today.Year - dob.Year;
        if (today < AnniversaryIn(dob, today.Year))
        {
            age--;
        }

        return age;
    }

    /// <summary>
    /// The date a birth anniversary falls in a given year.
    /// </summary>
    /// <remarks>
    /// A 29 February birth has no anniversary in a non-leap year. The anniversary is
    /// taken as 1 March rather than 28 February, so a client born on a leap day is
    /// treated as still 17 for that extra day. DateOnly.AddYears would clamp to
    /// 28 February and make them an adult a day early; because this gates guardian
    /// consent, the boundary is set so an error requires consent rather than skips it.
    /// </remarks>
    private static DateOnly AnniversaryIn(DateOnly dateOfBirth, int year)
    {
        if (dateOfBirth is { Month: 2, Day: 29 } && !DateTime.IsLeapYear(year))
        {
            return new DateOnly(year, 3, 1);
        }

        return new DateOnly(year, dateOfBirth.Month, dateOfBirth.Day);
    }

    /// <summary>
    /// Whether the guardian workflow in specification section 11 applies. Unknown date of
    /// birth is treated as requiring consent, so an incomplete profile cannot bypass the check.
    /// </summary>
    public bool RequiresGuardianConsent(DateOnly today) => AgeOn(today) is not { } age || age < 18;

    /// <summary>
    /// Whether the client is barred from purchase and publication because guardian
    /// approval is required and has not been recorded (specification section 11).
    /// </summary>
    /// <remarks>
    /// Retouching is deliberately not gated on this. The studio workflow is meant to be
    /// fast, so preparation begins while the guardian's approval is outstanding; the
    /// hard stop is the one the specification states, at purchase and publication.
    /// </remarks>
    public bool IsBlockedPendingGuardianConsent(DateOnly today) =>
        RequiresGuardianConsent(today) && GuardianConsent?.IsApproved != true;

    // ── A suggested biography ────────────────────────────────────────────────────
    //
    // Asked for once, when an administrator approves the portfolio, and only ever
    // offered as a draft. Writing straight into Biography would put text nobody had
    // read onto the public page of a real person.

    public BiographyDraftStatus BiographyDraftStatus { get; set; } = BiographyDraftStatus.NotRequested;

    /// <summary>The suggestion itself, until it is accepted or thrown away.</summary>
    public string? BiographyDraft { get; set; }

    public DateTimeOffset? BiographyDraftGeneratedAt { get; set; }

    /// <summary>Why it could not be written, shown rather than swallowed.</summary>
    public string? BiographyDraftError { get; set; }

    public int BiographyDraftAttempts { get; set; }

    public DateTimeOffset? BiographyDraftNextAttemptAt { get; set; }

    public bool NeedsBiographyDraft(DateTimeOffset now) =>
        BiographyDraftStatus is BiographyDraftStatus.Pending
        && (BiographyDraftNextAttemptAt is null || BiographyDraftNextAttemptAt <= now);

    /// <summary>
    /// Asks for a draft, once and once only.
    /// </summary>
    /// <remarks>
    /// Refused when a biography already exists: that text was written or approved by a
    /// person, and quietly queueing a replacement for it is not what anyone asked for.
    /// Refused in every state but the first, so approving a portfolio a second time — or
    /// an administrator who has already thrown one draft away — does not produce another.
    /// </remarks>
    /// <summary>
    /// Finishes with a suggestion once the biography has been saved.
    /// </summary>
    /// <remarks>
    /// Saving a biography is how a suggestion gets accepted, because the suggestion is
    /// put into the box for editing rather than applied behind the scenes. Without this
    /// the box would keep refilling itself on every visit, and the suggestion would be
    /// offered again over text somebody had already written.
    /// </remarks>
    public void CloseBiographyDraftIfSaved()
    {
        if (BiographyDraftStatus is not BiographyDraftStatus.Ready
            || string.IsNullOrWhiteSpace(Biography))
        {
            return;
        }

        BiographyDraft = null;
        BiographyDraftStatus = BiographyDraftStatus.Closed;
    }

    public bool RequestBiographyDraft()
    {
        if (BiographyDraftStatus is not BiographyDraftStatus.NotRequested
            || !string.IsNullOrWhiteSpace(Biography))
        {
            return false;
        }

        BiographyDraftStatus = BiographyDraftStatus.Pending;
        BiographyDraftNextAttemptAt = null;

        return true;
    }
}
