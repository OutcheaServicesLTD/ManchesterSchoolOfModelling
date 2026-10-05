using Msm.Portfolio.Web.Domain.Enums;

namespace Msm.Portfolio.Web.ViewModels;

public record CrmFailureRow(
    Guid ClientId,
    string ClientName,
    int Attempts,
    string? Error,
    DateTimeOffset? NextAttemptAt);

/// <summary>The integration status page (specification section 4).</summary>
public class IntegrationsViewModel
{
    public bool CrmIsLive { get; set; }

    /// <summary>
    /// Whether Stripe is configured — covers both the £99 one-off portfolio purchase
    /// and the portfolio-maintenance subscription, one account behind both.
    /// </summary>
    public bool StripeIsLive { get; set; }

    public IReadOnlyDictionary<CrmSyncStatus, int> CrmStates { get; set; } =
        new Dictionary<CrmSyncStatus, int>();

    public List<CrmFailureRow> RecentCrmFailures { get; set; } = [];

    public int WebhookEventsReceived { get; set; }

    public int WebhookEventsFailed { get; set; }

    public int CountFor(CrmSyncStatus status) =>
        CrmStates.TryGetValue(status, out var count) ? count : 0;

    public int AwaitingSync => CountFor(CrmSyncStatus.Pending) + CountFor(CrmSyncStatus.Failed);
}
