namespace Msm.Portfolio.Web.Integrations.Bio;

/// <summary>
/// Settings for the suggested biography (specification sections 10 and 46).
/// </summary>
public class BiographyOptions
{
    public const string SectionName = "Biography";

    /// <summary>
    /// The API key. Absent means the feature is off and no draft is ever requested.
    /// </summary>
    /// <remarks>
    /// Belongs in user secrets or an environment variable, never in a committed file.
    /// </remarks>
    public string? ApiKey { get; set; }

    public string Model { get; set; } = "claude-opus-5";

    /// <summary>
    /// Roughly how long the biography should be, in words. Kept as a setting because the
    /// right length is an editorial decision for the studio, not a constant.
    /// </summary>
    public int TargetWords { get; set; } = 90;

    /// <summary>
    /// How many times a failed draft is retried before it is left alone.
    /// </summary>
    public int MaxAttempts { get; set; } = 3;
}
