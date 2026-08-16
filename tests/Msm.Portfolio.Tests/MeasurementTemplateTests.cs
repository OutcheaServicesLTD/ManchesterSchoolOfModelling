using Microsoft.Extensions.Options;
using Msm.Portfolio.Web.Configuration;
using Msm.Portfolio.Web.Domain.Enums;
using Msm.Portfolio.Web.Services;

namespace Msm.Portfolio.Tests;

public class MeasurementTemplateTests
{
    private static MeasurementTemplateProvider Provider(MeasurementTemplateOptions? options = null)
    {
        var monitor = new StaticOptionsMonitor<MeasurementTemplateOptions>(
            options ?? new MeasurementTemplateOptions());

        return new MeasurementTemplateProvider(monitor);
    }

    [Fact]
    public void Female_template_matches_the_specification_defaults()
    {
        var keys = Provider().GetTemplate(ModelProfileType.Female).Select(f => f.Key);

        Assert.Equal(
            ["Height", "Bust", "Waist", "Hips", "DressSize", "ShoeSize"],
            keys);
    }

    [Fact]
    public void Male_template_matches_the_specification_defaults()
    {
        var keys = Provider().GetTemplate(ModelProfileType.Male).Select(f => f.Key);

        Assert.Equal(
            ["Height", "Chest", "Waist", "InsideLeg", "Collar", "SuitSize", "ShoeSize"],
            keys);
    }

    [Fact]
    public void An_unspecified_profile_type_has_no_measurements()
    {
        Assert.Empty(Provider().GetTemplate(ModelProfileType.Unspecified));
    }

    /// <summary>
    /// Specification section 9 requires the fields to be changeable without a schema
    /// change, so a configured template must win over the built-in default.
    /// </summary>
    [Fact]
    public void A_configured_template_replaces_the_built_in_default()
    {
        var options = new MeasurementTemplateOptions
        {
            Templates =
            {
                ["Female"] =
                [
                    new MeasurementFieldDefinition
                    {
                        Key = "Height", Label = "Height",
                        Unit = MeasurementUnit.Centimetres, DisplayOrder = 1
                    },
                    new MeasurementFieldDefinition
                    {
                        Key = "Reach", Label = "Reach",
                        Unit = MeasurementUnit.Centimetres, DisplayOrder = 2
                    }
                ]
            }
        };

        var keys = Provider(options).GetTemplate(ModelProfileType.Female).Select(f => f.Key);

        Assert.Equal(["Height", "Reach"], keys);
    }

    [Fact]
    public void Fields_are_returned_in_display_order()
    {
        var options = new MeasurementTemplateOptions
        {
            Templates =
            {
                ["Male"] =
                [
                    new MeasurementFieldDefinition { Key = "Third", DisplayOrder = 3 },
                    new MeasurementFieldDefinition { Key = "First", DisplayOrder = 1 },
                    new MeasurementFieldDefinition { Key = "Second", DisplayOrder = 2 }
                ]
            }
        };

        var keys = Provider(options).GetTemplate(ModelProfileType.Male).Select(f => f.Key);

        Assert.Equal(["First", "Second", "Third"], keys);
    }

    [Fact]
    public void Inches_are_normalised_to_centimetres()
    {
        Assert.Equal(170.18m, Provider().ToCanonical(67m, MeasurementUnit.Inches));
    }

    [Fact]
    public void Centimetres_are_kept_as_entered()
    {
        Assert.Equal(170.00m, Provider().ToCanonical(170m, MeasurementUnit.Centimetres));
    }

    /// <summary>
    /// A dress or shoe size is not a point on a continuous scale, so converting one
    /// would invent precision. Those fields deliberately have no canonical value.
    /// </summary>
    [Theory]
    [InlineData(MeasurementUnit.UkDressSize)]
    [InlineData(MeasurementUnit.UkShoeSize)]
    [InlineData(MeasurementUnit.UkSuitSize)]
    [InlineData(MeasurementUnit.Text)]
    [InlineData(MeasurementUnit.None)]
    public void Uk_sizes_have_no_canonical_value(MeasurementUnit unit)
    {
        Assert.Null(Provider().ToCanonical(12m, unit));
    }

    [Fact]
    public void Only_length_fields_offer_a_unit_choice()
    {
        var template = Provider().GetTemplate(ModelProfileType.Female);

        Assert.True(template.Single(f => f.Key == "Height").AllowsUnitChoice);
        Assert.False(template.Single(f => f.Key == "ShoeSize").AllowsUnitChoice);
    }
}

/// <summary>Minimal IOptionsMonitor for tests that do not need change notification.</summary>
internal class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue { get; } = value;

    public T Get(string? name) => CurrentValue;

    public IDisposable? OnChange(Action<T, string?> listener) => null;
}
