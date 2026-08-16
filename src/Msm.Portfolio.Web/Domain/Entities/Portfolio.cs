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

    /// <summary>Result of the last CRM push, per specification section 45.</summary>
    public CrmSyncStatus CrmSyncStatus { get; set; } = CrmSyncStatus.NotSynced;

    public DateTimeOffset? CrmSyncedAt { get; set; }

    public string? CrmSyncError { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// True when the portfolio should be reachable at its public URL. Publication is
    /// the single gate; status alone is not sufficient.
    /// </summary>
    public bool IsPubliclyVisible => IsPublished && !string.IsNullOrWhiteSpace(Slug);
}
