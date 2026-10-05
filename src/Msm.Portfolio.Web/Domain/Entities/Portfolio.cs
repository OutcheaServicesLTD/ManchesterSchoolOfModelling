using Msm.Portfolio.Web.Domain.Enums;

namespace Msm.Portfolio.Web.Domain.Entities;

/// <summary>
/// The model's portfolio and its lifecycle (specification sections 15, 27 and 26).
/// </summary>
public class Portfolio
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ClientId { get; set; }

    public ClientProfile Client { get; set; } = null!;

    /// <summary>
    /// Public URL segment, for example "emma-johnson" in /emma-johnson
    /// (specification section 39). Assigned at first publication and stable
    /// thereafter, so a link already shared with an agency does not break when the
    /// model changes their display name.
    /// </summary>
    public string? Slug { get; set; }

    public PortfolioStatus Status { get; set; } = PortfolioStatus.AwaitingClientInformation;

    /// <summary>Hero image, also used for the Model Board card (specification section 12).</summary>
    public Guid? FeaturedMediaId { get; set; }

    public MediaAsset? FeaturedMedia { get; set; }

    /// <summary>
    /// Drives public visibility. Held separately from <see cref="Status"/> because a
    /// portfolio can be live while carrying a payment warning.
    /// </summary>
    public bool IsPublished { get; set; }

    /// <summary>
    /// Whether the model appears on the public Model Board. Board eligibility also
    /// requires publication and an active entitlement (specification section 18).
    /// </summary>
    public bool IsVisibleOnModelBoard { get; set; } = true;

    public DateTimeOffset? PublishedAt { get; set; }

    public DateTimeOffset? UnpublishedAt { get; set; }

    /// <summary>
    /// When the purchased year runs out and the portfolio stops being public.
    /// </summary>
    /// <remarks>
    /// Null before anything is bought, and on portfolios sold under the old programme
    /// price, which carried no term. A null expiry never expires — it is not treated as
    /// "expired long ago", which would take down every portfolio sold before this existed.
    /// </remarks>
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>Days left of the purchased year, or null when there is no term.</summary>
    public int? DaysRemaining(DateTimeOffset now) =>
        ExpiresAt is { } expires ? (int)Math.Ceiling((expires - now).TotalDays) : null;

    /// <summary>True when the year is up and the portfolio should no longer be public.</summary>
    public bool HasExpired(DateTimeOffset now) => ExpiresAt is { } expires && expires <= now;

    /// <summary>Result of the last CRM push, per specification section 45.</summary>
    public CrmSyncStatus CrmSyncStatus { get; set; } = CrmSyncStatus.NotSynced;

    public DateTimeOffset? CrmSyncedAt { get; set; }

    public string? CrmSyncError { get; set; }

    /// <summary>
    /// Consecutive failed pushes. Drives the backoff and tells staff how long the CRM
    /// has been unreachable for this client.
    /// </summary>
    public int CrmSyncAttempts { get; set; }

    /// <summary>
    /// When the next push may be attempted. Kept on the row rather than in memory so a
    /// restart does not lose the backoff and immediately hammer a CRM that is down.
    /// </summary>
    public DateTimeOffset? CrmSyncNextAttemptAt { get; set; }

    /// <summary>Whether this portfolio is waiting to be pushed to the CRM.</summary>
    public bool NeedsCrmSync(DateTimeOffset now) =>
        CrmSyncStatus is CrmSyncStatus.Pending or CrmSyncStatus.Failed
        && (CrmSyncNextAttemptAt is null || CrmSyncNextAttemptAt <= now);

    /// <summary>
    /// Marks the portfolio as needing a push. Called after any change to a field the
    /// CRM shows, and deliberately never fails: a CRM problem must not disturb the
    /// operation that triggered it (specification section 45).
    /// </summary>
    public void RequestCrmSync()
    {
        CrmSyncStatus = CrmSyncStatus.Pending;
        CrmSyncNextAttemptAt = null;
    }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// True when the portfolio should be reachable at its public URL. Publication is
    /// the single gate; status alone is not sufficient.
    /// </summary>
    public bool IsPubliclyVisible => IsPublished && !string.IsNullOrWhiteSpace(Slug);
}
