using Msm.Portfolio.Web.Domain.Enums;

namespace Msm.Portfolio.Web.Configuration;

/// <summary>
/// One measurement the onboarding form collects (specification section 9).
/// </summary>
public class MeasurementFieldDefinition
{
    /// <summary>
    /// Stable key stored on the measurement row, for example "Height". Changing a key
    /// orphans previously captured values, so keys are treated as permanent.
    /// </summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Label shown to the client. Safe to reword at any time.</summary>
    public string Label { get; set; } = string.Empty;

    public MeasurementUnit Unit { get; set; } = MeasurementUnit.None;

    public bool Required { get; set; }

    public int DisplayOrder { get; set; }

    /// <summary>
    /// Bounds for numeric entry, catching a height typed in the wrong unit rather than
    /// letting an implausible figure reach an agency.
    /// </summary>
    public decimal? Minimum { get; set; }

    public decimal? Maximum { get; set; }

    /// <summary>
    /// Whether the client may pick the unit. Lengths accept centimetres or inches;
    /// UK sizes have no alternative unit.
    /// </summary>
    public bool AllowsUnitChoice => Unit is MeasurementUnit.Centimetres or MeasurementUnit.Inches;
}

/// <summary>
/// Which measurements are collected for each model profile type.
/// </summary>
/// <remarks>
/// Held in configuration rather than in code or columns, because the specification
/// requires MSM to be able to change the fields without redesigning the database.
/// Values captured against a removed field remain on the client's record.
/// </remarks>
public class MeasurementTemplateOptions
{
    public const string SectionName = "MeasurementTemplates";

    /// <summary>
    /// Templates keyed by <see cref="ModelProfileType"/> name. Anything configured here
    /// replaces the built-in defaults for that profile type.
    /// </summary>
    public Dictionary<string, MeasurementFieldDefinition[]> Templates { get; set; } = new();

    /// <summary>
    /// The defaults from specification section 9, used when configuration supplies no
    /// template. Hair and eye colour are not here: they are columns on the client
    /// profile per specification section 26, and the form presents them alongside these.
    /// </summary>
    public static IReadOnlyDictionary<ModelProfileType, MeasurementFieldDefinition[]> Defaults { get; } =
        new Dictionary<ModelProfileType, MeasurementFieldDefinition[]>
        {
            [ModelProfileType.Female] =
            [
                Length("Height", "Height", 1, required: true, min: 100, max: 220),
                Length("Bust", "Bust", 2, required: true, min: 50, max: 200),
                Length("Waist", "Waist", 3, required: true, min: 40, max: 200),
                Length("Hips", "Hips", 4, required: true, min: 50, max: 200),
                Size("DressSize", "Dress size", MeasurementUnit.UkDressSize, 5),
                Size("ShoeSize", "Shoe size", MeasurementUnit.UkShoeSize, 6)
            ],
            [ModelProfileType.Male] =
            [
                Length("Height", "Height", 1, required: true, min: 100, max: 230),
                Length("Chest", "Chest", 2, required: true, min: 60, max: 200),
                Length("Waist", "Waist", 3, required: true, min: 50, max: 200),
                Length("InsideLeg", "Inside leg", 4, required: false, min: 50, max: 130),
                Length("Collar", "Collar", 5, required: false, min: 25, max: 60),
                Size("SuitSize", "Suit or jacket size", MeasurementUnit.UkSuitSize, 6),
                Size("ShoeSize", "Shoe size", MeasurementUnit.UkShoeSize, 7)
            ]
        };

    private static MeasurementFieldDefinition Length(
        string key, string label, int order, bool required, decimal min, decimal max) => new()
        {
            Key = key,
            Label = label,
            Unit = MeasurementUnit.Centimetres,
            Required = required,
            DisplayOrder = order,
            Minimum = min,
            Maximum = max
        };

    private static MeasurementFieldDefinition Size(
        string key, string label, MeasurementUnit unit, int order) => new()
        {
            Key = key,
            Label = label,
            Unit = unit,
            Required = false,
            DisplayOrder = order
        };
}
