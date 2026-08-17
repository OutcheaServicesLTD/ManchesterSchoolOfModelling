namespace Msm.Portfolio.Web.Integrations.HighLevel;

/// <summary>
/// The portfolio fields mirrored onto a GoHighLevel contact (specification section 25).
/// </summary>
/// <remarks>
/// Deliberately a small, named set rather than an open dictionary. These are the fields
/// the specification lists, and keeping them explicit means nothing else about a client
/// — their measurements, their photographs, their guardian's details — can be pushed to
/// an external system by accident.
/// </remarks>
public record CrmContactFields(
    string? PortfolioUrl,
    string PortfolioStatus,
    string PurchaseStatus,
    DateTimeOffset? PurchaseDate,
    string MaintenanceStatus,
    DateTimeOffset? PortfolioPublishedDate);

public record CrmUpdateResult(bool Succeeded, string? Error = null, bool IsRetryable = true);

/// <summary>
/// The CRM boundary (specification sections 25 and 45).
/// </summary>
public interface IHighLevelService
{
    /// <summary>True when a real CRM is configured; false when running on the stub.</summary>
    bool IsLive { get; }

    Task<CrmUpdateResult> UpdateContactAsync(
        string contactId, CrmContactFields fields, CancellationToken cancellationToken = default);
}

/// <summary>
/// Logs what would be sent instead of sending it.
/// </summary>
/// <remarks>
/// Registered whenever no GoHighLevel API key is configured. It reports success, which
/// is correct here: the CRM is a mirror of state held in this application, so with no
/// CRM connected there is genuinely nothing outstanding. Treating it as a failure would
/// fill the retry queue and alert staff about an integration nobody has set up yet.
/// </remarks>
public class StubHighLevelService(
    ILogger<StubHighLevelService> logger,
    IHostEnvironment environment) : IHighLevelService
{
    public bool IsLive => false;

    public Task<CrmUpdateResult> UpdateContactAsync(
        string contactId, CrmContactFields fields, CancellationToken cancellationToken = default)
    {
        if (environment.IsDevelopment())
        {
            logger.LogInformation(
                "CRM update (not sent, no provider configured) for contact {ContactId}: "
                + "status={Status}, url={Url}, purchase={Purchase}, maintenance={Maintenance}",
                contactId, fields.PortfolioStatus, fields.PortfolioUrl,
                fields.PurchaseStatus, fields.MaintenanceStatus);
        }
        else
        {
            // Outside development, silently pretending to sync would leave MSM believing
            // their CRM was up to date when nothing had been sent.
            logger.LogWarning(
                "No GoHighLevel provider is configured, so contact {ContactId} was not updated.",
                contactId);
        }

        return Task.FromResult(new CrmUpdateResult(true));
    }
}
