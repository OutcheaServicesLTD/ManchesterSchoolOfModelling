using System.ComponentModel.DataAnnotations;
using Msm.Portfolio.Web.Configuration;
using Msm.Portfolio.Web.Domain.Enums;

namespace Msm.Portfolio.Web.ViewModels;

/// <summary>
/// The onboarding form the client completes after their photoshoot
/// (specification sections 8, 9, 10 and 11).
/// </summary>
public class OnboardingViewModel : IValidatableObject
{
    /// <summary>
    /// Carried from the GoHighLevel link and never shown to the client
    /// (specification section 8).
    /// </summary>
    public string? GhlContactId { get; set; }

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

    [Required(ErrorMessage = "Please enter your email address.")]
    [EmailAddress(ErrorMessage = "Please enter a valid email address.")]
    [StringLength(256)]
    [Display(Name = "Email address")]
    public string Email { get; set; } = string.Empty;

    [Phone(ErrorMessage = "Please enter a valid telephone number.")]
    [StringLength(50)]
    [Display(Name = "Telephone number")]
    public string? Phone { get; set; }

    [Required(ErrorMessage = "Please enter your date of birth.")]
    [DataType(DataType.Date)]
    [Display(Name = "Date of birth")]
    public DateOnly? DateOfBirth { get; set; }

    [StringLength(200)]
    [Display(Name = "Location")]
    public string? Location { get; set; }

    [Required(ErrorMessage = "Please choose a profile type.")]
    [Display(Name = "Profile type")]
    public ModelProfileType ModelProfileType { get; set; } = ModelProfileType.Unspecified;

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

    /// <summary>
    /// True once the entered date of birth shows the client is under 18, which makes
    /// the guardian fields mandatory (specification section 11).
    /// </summary>
    public bool GuardianRequired { get; set; }

    [StringLength(200)]
    [Display(Name = "Guardian's full name")]
    public string? GuardianName { get; set; }

    [StringLength(100)]
    [Display(Name = "Relationship to you")]
    public string? GuardianRelationship { get; set; }

    [EmailAddress(ErrorMessage = "Please enter a valid email address.")]
    [StringLength(256)]
    [Display(Name = "Guardian's email address")]
    public string? GuardianEmail { get; set; }

    [Phone(ErrorMessage = "Please enter a valid telephone number.")]
    [StringLength(50)]
    [Display(Name = "Guardian's telephone number")]
    public string? GuardianPhone { get; set; }

    /// <summary>Field definitions for the chosen profile type, used to render the form.</summary>
    public IReadOnlyList<MeasurementFieldDefinition> Template { get; set; } = [];

    /// <summary>
    /// Rules that depend on more than one field, so they cannot be expressed as
    /// attributes. All of these run server-side regardless of the browser
    /// (specification section 38).
    /// </summary>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        if (DateOfBirth is { } dob)
        {
            if (dob > today)
            {
                yield return new ValidationResult(
                    "Date of birth cannot be in the future.", [nameof(DateOfBirth)]);
            }
            else if (dob < today.AddYears(-100))
            {
                yield return new ValidationResult(
                    "Please check the date of birth.", [nameof(DateOfBirth)]);
            }
        }

        if (ModelProfileType == ModelProfileType.Unspecified)
        {
            yield return new ValidationResult(
                "Please choose a profile type.", [nameof(ModelProfileType)]);
        }

        // Guardian details are required from the date of birth itself, not from the
        // GuardianRequired flag the browser posted back, which a client could clear.
        var minor = DateOfBirth is not { } dateOfBirth
                    || new Domain.Entities.ClientProfile { DateOfBirth = dateOfBirth }.AgeOn(today) < 18;

        if (DateOfBirth is not null && minor)
        {
            if (string.IsNullOrWhiteSpace(GuardianName))
            {
                yield return new ValidationResult(
                    "A legal guardian's name is required for applicants under 18.",
                    [nameof(GuardianName)]);
            }

            if (string.IsNullOrWhiteSpace(GuardianRelationship))
            {
                yield return new ValidationResult(
                    "Please tell us the guardian's relationship to you.",
                    [nameof(GuardianRelationship)]);
            }

            if (string.IsNullOrWhiteSpace(GuardianEmail))
            {
                yield return new ValidationResult(
                    "A legal guardian's email address is required for applicants under 18.",
                    [nameof(GuardianEmail)]);
            }
            else if (string.Equals(GuardianEmail, Email, StringComparison.OrdinalIgnoreCase))
            {
                // Consent has to come from the guardian, so it cannot arrive at the
                // applicant's own inbox.
                yield return new ValidationResult(
                    "The guardian's email address must be different from your own.",
                    [nameof(GuardianEmail)]);
            }
        }

        foreach (var field in Template.Where(f => f.Required))
        {
            var entered = Measurements.FirstOrDefault(m => m.Key == field.Key);

            if (entered is null || string.IsNullOrWhiteSpace(entered.Value))
            {
                yield return new ValidationResult(
                    $"Please enter your {field.Label.ToLowerInvariant()}.",
                    [nameof(Measurements)]);
            }
        }
    }
}

/// <summary>One measurement as entered on the form.</summary>
public class MeasurementInputModel
{
    public string Key { get; set; } = string.Empty;

    public string? Value { get; set; }

    /// <summary>
    /// Unit the client chose, where the field allows a choice. Lengths accept
    /// centimetres or inches (specification section 9).
    /// </summary>
    public MeasurementUnit Unit { get; set; } = MeasurementUnit.None;
}
