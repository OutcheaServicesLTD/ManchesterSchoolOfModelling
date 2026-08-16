using Msm.Portfolio.Web.Domain.Enums;

namespace Msm.Portfolio.Web.Domain.Entities;

/// <summary>
/// Record of an inbound provider webhook (specification sections 26 and 44).
/// <see cref="ProviderEventId"/> is unique, which is what makes webhook handling
/// idempotent: a provider retry finds the event already stored and does nothing.
/// </summary>
public class PaymentWebhookEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Provider { get; set; } = "GoCardless";

    /// <summary>The provider's event identifier. Unique per provider.</summary>
    public string ProviderEventId { get; set; } = string.Empty;

    public string EventType { get; set; } = string.Empty;

    /// <summary>Raw payload, retained so an event can be re-examined or replayed.</summary>
    public string? Payload { get; set; }

    public DateTimeOffset ReceivedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? ProcessedAt { get; set; }

    public WebhookProcessingStatus ProcessingStatus { get; set; } = WebhookProcessingStatus.Received;

    public string? ProcessingError { get; set; }
}

/// <summary>
/// Append-only record of significant actions (specification sections 26 and 36).
/// Written for every action in the section 36 list, including client self-service
/// edits made after publication.
/// </summary>
public class AuditLog
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Null for system-initiated actions such as webhook processing.</summary>
    public Guid? UserId { get; set; }

    public ApplicationUser? User { get; set; }

    public string EntityType { get; set; } = string.Empty;

    public string EntityId { get; set; } = string.Empty;

    public string Action { get; set; } = string.Empty;

    public string? OldValue { get; set; }

    public string? NewValue { get; set; }

    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>In-application notification for staff or a client (specification sections 26 and 37).</summary>
public class Notification
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid UserId { get; set; }

    public ApplicationUser User { get; set; } = null!;

    public string Type { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;

    /// <summary>Optional deep link to the record the notification concerns.</summary>
    public string? Url { get; set; }

    public bool IsRead { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Runtime-editable configuration (specification section 26), covering the values the
/// specification requires to be changeable without a code change: image limits, the
/// maintenance price, and the grace period length. Keys are listed in
/// <see cref="Configuration.SystemSettingKeys"/>.
/// </summary>
public class SystemSetting
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Key { get; set; } = string.Empty;

    public string? Value { get; set; }

    public string? Description { get; set; }

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public Guid? UpdatedByUserId { get; set; }
}
