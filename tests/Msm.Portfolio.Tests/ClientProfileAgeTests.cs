using Msm.Portfolio.Web.Domain.Entities;

namespace Msm.Portfolio.Tests;

/// <summary>
/// Age drives the guardian consent requirement in specification section 11, so the
/// boundary around an eighteenth birthday is pinned down explicitly.
/// </summary>
public class ClientProfileAgeTests
{
    private static readonly DateOnly Today = new(2026, 8, 16);

    private static ClientProfile WithDateOfBirth(DateOnly? dob) =>
        new() { FirstName = "Test", LastName = "Model", DateOfBirth = dob };

    [Fact]
    public void Age_is_calculated_from_date_of_birth()
    {
        Assert.Equal(26, WithDateOfBirth(new DateOnly(2000, 1, 1)).AgeOn(Today));
    }

    [Fact]
    public void Age_does_not_count_a_birthday_that_has_not_happened_yet_this_year()
    {
        // Birthday falls the day after "today", so they are still 17.
        var client = WithDateOfBirth(new DateOnly(2008, 8, 17));

        Assert.Equal(17, client.AgeOn(Today));
        Assert.True(client.RequiresGuardianConsent(Today));
    }

    [Fact]
    public void Turning_eighteen_today_removes_the_guardian_requirement()
    {
        var client = WithDateOfBirth(new DateOnly(2008, 8, 16));

        Assert.Equal(18, client.AgeOn(Today));
        Assert.False(client.RequiresGuardianConsent(Today));
    }

    [Fact]
    public void A_client_one_day_short_of_eighteen_still_requires_consent()
    {
        var client = WithDateOfBirth(new DateOnly(2008, 8, 17));

        Assert.True(client.RequiresGuardianConsent(Today));
    }

    /// <summary>
    /// An incomplete profile must not be treated as an adult, otherwise a minor could
    /// pass the check simply by leaving the field blank.
    /// </summary>
    [Fact]
    public void Missing_date_of_birth_requires_guardian_consent()
    {
        var client = WithDateOfBirth(null);

        Assert.Null(client.AgeOn(Today));
        Assert.True(client.RequiresGuardianConsent(Today));
    }

    [Fact]
    public void Leap_day_birthday_is_counted_from_the_first_of_march_in_a_non_leap_year()
    {
        var client = WithDateOfBirth(new DateOnly(2008, 2, 29));

        // 2026 is not a leap year: on 28 February they are still 17.
        Assert.Equal(17, client.AgeOn(new DateOnly(2026, 2, 28)));
        Assert.Equal(18, client.AgeOn(new DateOnly(2026, 3, 1)));
    }

    [Fact]
    public void Public_name_prefers_the_display_name_when_one_is_set()
    {
        var client = WithDateOfBirth(new DateOnly(2000, 1, 1));
        Assert.Equal("Test Model", client.PublicName);

        client.DisplayName = "Testa";
        Assert.Equal("Testa", client.PublicName);
    }
}
