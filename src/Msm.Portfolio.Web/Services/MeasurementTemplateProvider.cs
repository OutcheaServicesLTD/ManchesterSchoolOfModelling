using Microsoft.Extensions.Options;
using Msm.Portfolio.Web.Configuration;
using Msm.Portfolio.Web.Domain.Enums;

namespace Msm.Portfolio.Web.Services;

/// <summary>Supplies the measurement fields for a model profile type.</summary>
public interface IMeasurementTemplateProvider
{
    IReadOnlyList<MeasurementFieldDefinition> GetTemplate(ModelProfileType profileType);

    /// <summary>
    /// Converts an entered value to the canonical internal form, so measurements
    /// captured in different units stay comparable (specification section 9).
    /// Returns null when the value has no meaningful numeric form, such as a UK size.
    /// </summary>
    decimal? ToCanonical(decimal value, MeasurementUnit unit);
}

public class MeasurementTemplateProvider(IOptionsMonitor<MeasurementTemplateOptions> options)
    : IMeasurementTemplateProvider
{
    private const decimal CentimetresPerInch = 2.54m;

    public IReadOnlyList<MeasurementFieldDefinition> GetTemplate(ModelProfileType profileType)
    {
        var configured = options.CurrentValue.Templates;

        if (configured.TryGetValue(profileType.ToString(), out var fields) && fields.Length > 0)
        {
            return [.. fields.OrderBy(f => f.DisplayOrder)];
        }

        return MeasurementTemplateOptions.Defaults.TryGetValue(profileType, out var defaults)
            ? [.. defaults.OrderBy(f => f.DisplayOrder)]
            : [];
    }

    /// <summary>
    /// Lengths normalise to centimetres. UK sizes are deliberately left without a
    /// canonical value: a dress size is not a measurement on a continuous scale, and
    /// converting one would invent precision that does not exist.
    /// </summary>
    public decimal? ToCanonical(decimal value, MeasurementUnit unit) => unit switch
    {
        MeasurementUnit.Centimetres => decimal.Round(value, 2),
        MeasurementUnit.Inches => decimal.Round(value * CentimetresPerInch, 2),
        _ => null
    };
}
