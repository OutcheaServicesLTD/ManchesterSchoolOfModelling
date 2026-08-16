using System.ComponentModel.DataAnnotations;
using Msm.Portfolio.Web.Configuration;
using Msm.Portfolio.Web.Domain.Entities;
using Msm.Portfolio.Web.Domain.Enums;
using Msm.Portfolio.Web.ViewModels;

namespace Msm.Portfolio.Tests;

/// <summary>
/// Onboarding is open to anonymous visitors, so every rule has to hold server-side
/// regardless of what the browser sent (specification section 38).
/// </summary>
public class OnboardingValidationTests
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);

    private static OnboardingViewModel ValidAdult() => new()
    {
        FirstName = "Emma",
        LastName = "Johnson",
        Email = "emma@example.com",
        DateOfBirth = Today.AddYears(-25),
        ModelProfileType = ModelProfileType.Female,
        Template = MeasurementTemplateOptions.Defaults[ModelProfileType.Female],
        Measurements =
        [
            new MeasurementInputModel { Key = "Height", Value = "170", Unit = MeasurementUnit.Centimetres },
            new MeasurementInputModel { Key = "Bust", Value = "86", Unit = MeasurementUnit.Centimetres },
            new MeasurementInputModel { Key = "Waist", Value = "66", Unit = MeasurementUnit.Centimetres },
            new MeasurementInputModel { Key = "Hips", Value = "92", Unit = MeasurementUnit.Centimetres }
        ]
    };

    private static List<ValidationResult> Validate(OnboardingViewModel model) =>
        [.. model.Validate(new ValidationContext(model))];

    private static bool HasErrorFor(List<ValidationResult> results, string member) =>
        results.Any(r => r.MemberNames.Contains(member));

    [Fact]
    public void A_complete_adult_submission_passes()
    {
        Assert.Empty(Validate(ValidAdult()));
    }

    [Fact]
    public void A_future_date_of_birth_is_rejected()
    {
        var model = ValidAdult();
        model.DateOfBirth = Today.AddDays(1);

        Assert.True(HasErrorFor(Validate(model), nameof(model.DateOfBirth)));
    }

    [Fact]
    public void A_profile_type_must_be_chosen()
    {
        var model = ValidAdult();
        model.ModelProfileType = ModelProfileType.Unspecified;

        Assert.True(HasErrorFor(Validate(model), nameof(model.ModelProfileType)));
    }

    [Fact]
    public void A_required_measurement_must_be_supplied()
    {
        var model = ValidAdult();
        model.Measurements.RemoveAll(m => m.Key == "Height");

        Assert.True(HasErrorFor(Validate(model), nameof(model.Measurements)));
    }

    [Fact]
    public void An_optional_measurement_may_be_left_blank()
    {
        var model = ValidAdult();
        model.Measurements.RemoveAll(m => m.Key == "ShoeSize");

        Assert.Empty(Validate(model));
    }

    [Fact]
    public void A_minor_must_supply_guardian_details()
    {
        var model = ValidAdult();
        model.DateOfBirth = Today.AddYears(-16);

        var results = Validate(model);

        Assert.True(HasErrorFor(results, nameof(model.GuardianName)));
        Assert.True(HasErrorFor(results, nameof(model.GuardianRelationship)));
        Assert.True(HasErrorFor(results, nameof(model.GuardianEmail)));
    }

    /// <summary>
    /// The form posts a GuardianRequired flag back, and a client could clear it. The
    /// requirement is derived from the date of birth instead, so tampering achieves
    /// nothing.
    /// </summary>
    [Fact]
    public void Clearing_the_guardian_required_flag_does_not_bypass_the_check()
    {
        var model = ValidAdult();
        model.DateOfBirth = Today.AddYears(-15);
        model.GuardianRequired = false;

        Assert.True(HasErrorFor(Validate(model), nameof(model.GuardianName)));
    }

    [Fact]
    public void A_minor_with_complete_guardian_details_passes()
    {
        var model = ValidAdult();
        model.DateOfBirth = Today.AddYears(-16);
        model.GuardianName = "Alex Johnson";
        model.GuardianRelationship = "Parent";
        model.GuardianEmail = "alex@example.com";

        Assert.Empty(Validate(model));
    }

    /// <summary>
    /// Consent has to come from the guardian, so the approval link cannot be sent to
    /// the applicant's own inbox.
    /// </summary>
    [Fact]
    public void The_guardian_email_must_differ_from_the_applicants()
    {
        var model = ValidAdult();
        model.DateOfBirth = Today.AddYears(-16);
        model.GuardianName = "Alex Johnson";
        model.GuardianRelationship = "Parent";
        model.GuardianEmail = "EMMA@example.com";

        Assert.True(HasErrorFor(Validate(model), nameof(model.GuardianEmail)));
    }

    [Fact]
    public void An_adult_needs_no_guardian_details()
    {
        var model = ValidAdult();
        model.DateOfBirth = Today.AddYears(-18);

        Assert.Empty(Validate(model));
    }
}

public class GuardianConsentBlockTests
{
    private static readonly DateOnly Today = new(2026, 8, 16);

    private static ClientProfile Client(int age) => new()
    {
        FirstName = "Test",
        LastName = "Model",
        DateOfBirth = Today.AddYears(-age)
    };

    [Fact]
    public void A_minor_without_consent_is_blocked_from_purchase_and_publication()
    {
        Assert.True(Client(16).IsBlockedPendingGuardianConsent(Today));
    }

    [Fact]
    public void A_minor_with_pending_consent_is_still_blocked()
    {
        var client = Client(16);
        client.GuardianConsent = new GuardianConsent { Status = GuardianConsentStatus.Pending };

        Assert.True(client.IsBlockedPendingGuardianConsent(Today));
    }

    [Fact]
    public void A_minor_with_approved_consent_is_not_blocked()
    {
        var client = Client(16);
        client.GuardianConsent = new GuardianConsent { Status = GuardianConsentStatus.Approved };

        Assert.False(client.IsBlockedPendingGuardianConsent(Today));
    }

    [Fact]
    public void An_expired_consent_blocks_again()
    {
        var client = Client(16);
        client.GuardianConsent = new GuardianConsent { Status = GuardianConsentStatus.Expired };

        Assert.True(client.IsBlockedPendingGuardianConsent(Today));
    }

    [Fact]
    public void An_adult_is_never_blocked()
    {
        Assert.False(Client(25).IsBlockedPendingGuardianConsent(Today));
    }

    /// <summary>
    /// A profile with no date of birth must not be treated as an adult, or the block
    /// could be skipped by leaving the field empty.
    /// </summary>
    [Fact]
    public void A_missing_date_of_birth_is_blocked()
    {
        var client = new ClientProfile { FirstName = "Test", LastName = "Model" };

        Assert.True(client.IsBlockedPendingGuardianConsent(Today));
    }
}
