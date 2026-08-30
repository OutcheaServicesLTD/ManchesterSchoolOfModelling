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

    public bool PaymentsIsLive { get; set; }

    /// <summary>Whether Stripe is configured for the portfolio-maintenance subscription.</summary>
    public bool SubscriptionsAreLive { get; set; }

    /// <summary>Whether a biography provider is configured, so the page can say.</summary>
    public bool BiographiesAreOn { get; set; }

    /// <summary>How many biographies are waiting to be written, and how many gave up.</summary>
    public int BiographiesPending { get; set; }

    public int BiographiesFailed { get; set; }

    public IReadOnlyDictionary<CrmSyncStatus, int> CrmStates { get; set; } =
        new Dictionary<CrmSyncStatus, int>();

    public List<CrmFailureRow> RecentCrmFailures { get; set; } = [];

    public int WebhookEventsReceived { get; set; }

    public int WebhookEventsFailed { get; set; }

    public int SubscriptionWebhookEventsReceived { get; set; }

    public int SubscriptionWebhookEventsFailed { get; set; }

    public int CountFor(CrmSyncStatus status) =>
        CrmStates.TryGetValue(status, out var count) ? count : 0;

    public int AwaitingSync => CountFor(CrmSyncStatus.Pending) + CountFor(CrmSyncStatus.Failed);
}
