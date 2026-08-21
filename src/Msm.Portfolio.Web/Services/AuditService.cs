using Msm.Portfolio.Web.Data;
using Msm.Portfolio.Web.Domain.Entities;

namespace Msm.Portfolio.Web.Services;

/// <summary>Records the actions listed in specification section 36.</summary>
public interface IAuditService
{
    /// <summary>
    /// Stages an audit entry. The caller saves it as part of their own transaction, so
    /// an action and its audit record commit together or not at all.
    /// </summary>
    void Record(
        string entityType,
        string entityId,
        string action,
        Guid? userId = null,
        string? oldValue = null,
        string? newValue = null);
}

public class AuditService(ApplicationDbContext db) : IAuditService
{
    public void Record(
        string entityType,
        string entityId,
        string action,
        Guid? userId = null,
        string? oldValue = null,
        string? newValue = null)
    {
        db.AuditLogs.Add(new AuditLog
        {
            EntityType = entityType,
            EntityId = entityId,
            Action = action,
            UserId = userId,
            OldValue = Truncate(oldValue),
            NewValue = Truncate(newValue),
            Timestamp = DateTimeOffset.UtcNow
        });
    }

    // Audit values are free text and could otherwise exceed what the column accepts,
    // which would fail the whole transaction the audited action belongs to.
    private static string? Truncate(string? value) =>
        value is { Length: > 4000 } ? value[..4000] : value;
}

/// <summary>Action names used across the audit log, kept in one place so they stay consistent.</summary>
public static class AuditActions
{
    public const string ProfileCreated = "ProfileCreated";
    public const string ProfileEdited = "ProfileEdited";
    public const string OnboardingSubmitted = "OnboardingSubmitted";
    public const string GuardianConsentRequested = "GuardianConsentRequested";
    public const string GuardianConsentApproved = "GuardianConsentApproved";
    public const string MeasurementsUpdated = "MeasurementsUpdated";
    public const string PortfolioStatusChanged = "PortfolioStatusChanged";
    public const string PortfolioPublished = "PortfolioPublished";
    public const string PortfolioUnpublished = "PortfolioUnpublished";
    public const string PortfolioDeletedPermanently = "PortfolioDeletedPermanently";
    public const string SlugChanged = "SlugChanged";
    public const string StaffAccountCreated = "StaffAccountCreated";
    public const string StaffAccountDisabled = "StaffAccountDisabled";
    public const string StaffAccountEnabled = "StaffAccountEnabled";
    public const string StaffPasswordReset = "StaffPasswordReset";
    public const string PermissionsChanged = "PermissionsChanged";
    public const string AdminEditedClient = "AdminEditedClient";

    /// <summary>A model was given, or given back, the means to sign in.</summary>
    public const string ClientAccessIssued = "ClientAccessIssued";
    public const string CheckoutOpened = "CheckoutOpened";
    public const string CheckoutStarted = "CheckoutStarted";
    public const string CheckoutCancelled = "CheckoutCancelled";
    public const string PaymentConfirmed = "PaymentConfirmed";
    public const string PaymentFailed = "PaymentFailed";
    public const string PaymentStateChanged = "PaymentStateChanged";
    public const string WebhookProcessed = "WebhookProcessed";
    public const string MaintenancePaymentFailed = "MaintenancePaymentFailed";
    public const string MaintenancePaymentResolved = "MaintenancePaymentResolved";
    public const string MaintenanceGracePeriodExpired = "MaintenanceGracePeriodExpired";
    public const string MaintenanceActivated = "MaintenanceActivated";
}
