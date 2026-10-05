using Msm.Portfolio.Web.Domain.Enums;

namespace Msm.Portfolio.Web.Domain.Entities;

/// <summary>
/// One measurement captured against a client (specification sections 9 and 26).
/// Held as rows rather than dozens of fixed columns so MSM can change which
/// measurements they collect without a schema migration.
/// </summary>
public class ModelMeasurement
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ClientId { get; set; }

    public ClientProfile Client { get; set; } = null!;

    /// <summary>
    /// Measurement key, for example Height or Bust. Matches a key from the
    /// configured measurement template for the client's profile type.
    /// </summary>
    public string MeasurementType { get; set; } = string.Empty;

    /// <summary>
    /// Value as entered and displayed. Kept as text because UK sizes are not all
    /// numeric; the canonical numeric form lives in <see cref="CanonicalValue"/>.
    /// </summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>
    /// Value normalised to a single internal unit (centimetres for lengths) so that
    /// measurements captured in different units remain comparable, per specification section 9.
    /// Null where the measurement has no meaningful numeric form.
    /// </summary>
    public decimal? CanonicalValue { get; set; }

    public MeasurementUnit Unit { get; set; } = MeasurementUnit.None;

    public int DisplayOrder { get; set; }

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
