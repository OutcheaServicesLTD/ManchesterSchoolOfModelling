namespace Msm.Portfolio.Web.Integrations.Bio;

/// <summary>
/// Everything the writer is allowed to know about a model.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately a small, explicit record rather than the client entity. The entity holds
/// an email address, a telephone number, a date of birth, a CRM identifier and guardian
/// details, none of which belong in a request to anybody's API. Building a separate shape
/// means they cannot leak by accident when a field is added later.
/// </para>
/// <para>
/// No photographs are sent, only these facts. A biography is written from what the studio
/// recorded, and sending a young person's photographs to a third party to have their
/// appearance described is not something this feature needs in order to work.
/// </para>
/// </remarks>
public record BiographyFacts(
    string Name,
    string? Location,
    int? Age,
    string ProfileType,
    IReadOnlyList<BiographyMeasurement> Measurements,
    bool HasSelfTape,
    int PhotographCount);

public record BiographyMeasurement(string Label, string Value, string? Unit);

public record BiographyDraftResult(bool Succeeded, string? Text, string? Error);

public interface IBiographyWriter
{
    /// <summary>
    /// False when no provider is configured, in which case no draft is ever asked for.
    /// </summary>
    /// <remarks>
    /// Checked before a draft is requested rather than when one is written, so an
    /// unconfigured studio never accumulates a queue of pending drafts that can only
    /// fail.
    /// </remarks>
    bool IsEnabled { get; }

    Task<BiographyDraftResult> WriteAsync(BiographyFacts facts, CancellationToken cancellationToken = default);
}

/// <summary>
/// What runs when no provider is configured: nothing.
/// </summary>
public class StubBiographyWriter(ILogger<StubBiographyWriter> logger) : IBiographyWriter
{
    public bool IsEnabled => false;

    public Task<BiographyDraftResult> WriteAsync(
        BiographyFacts facts, CancellationToken cancellationToken = default)
    {
        logger.LogInformation(
            "Biography draft not written for {Name}: no provider is configured.", facts.Name);

        return Task.FromResult(new BiographyDraftResult(
            false, null, "No biography provider is configured."));
    }
}
