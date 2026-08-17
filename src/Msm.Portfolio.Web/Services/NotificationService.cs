using Microsoft.EntityFrameworkCore;
using Msm.Portfolio.Web.Authorization;
using Msm.Portfolio.Web.Data;
using Msm.Portfolio.Web.Domain.Entities;

namespace Msm.Portfolio.Web.Services;

/// <summary>In-application notifications for staff and clients (specification section 37).</summary>
public interface INotificationService
{
    void NotifyUser(Guid userId, string type, string message, string? url = null);

    /// <summary>
    /// Notifies everyone who can act on a studio event, such as a portfolio arriving
    /// for review. Staged for the caller's transaction like audit entries.
    /// </summary>
    Task NotifyStaffAsync(string type, string message, string? url = null, CancellationToken cancellationToken = default);
}

public class NotificationService(ApplicationDbContext db) : INotificationService
{
    public void NotifyUser(Guid userId, string type, string message, string? url = null)
    {
        db.Notifications.Add(new Notification
        {
            UserId = userId,
            Type = type,
            Message = message,
            Url = url
        });
    }

    public async Task NotifyStaffAsync(
        string type,
        string message,
        string? url = null,
        CancellationToken cancellationToken = default)
    {
        // Retouchers are excluded: section 37's staff notifications concern review,
        // payment and publication, none of which a retoucher acts on.
        var staffRoles = new[] { Roles.SuperAdmin, Roles.Admin };

        var recipientIds = await (
            from user in db.Users
            join userRole in db.UserRoles on user.Id equals userRole.UserId
            join role in db.Roles on userRole.RoleId equals role.Id
            where role.Name != null && staffRoles.Contains(role.Name) && user.IsActive
            select user.Id)
            .Distinct()
            .ToListAsync(cancellationToken);

        foreach (var recipientId in recipientIds)
        {
            NotifyUser(recipientId, type, message, url);
        }
    }
}

/// <summary>Notification type keys from specification section 37.</summary>
public static class NotificationTypes
{
    public const string OnboardingCompleted = "OnboardingCompleted";
    public const string GuardianApprovalRequired = "GuardianApprovalRequired";
    public const string GuardianApprovalReceived = "GuardianApprovalReceived";
    public const string PortfolioReadyForReview = "PortfolioReadyForReview";
    public const string PortfolioReturnedToRetoucher = "PortfolioReturnedToRetoucher";
    public const string PortfolioPublished = "PortfolioPublished";
    public const string PortfolioUnpublished = "PortfolioUnpublished";
    public const string EnquiryReceived = "EnquiryReceived";
    public const string PublicationBlockedAfterPurchase = "PublicationBlockedAfterPurchase";
    public const string PaymentFailed = "PaymentFailed";
    public const string PurchaseConfirmed = "PurchaseConfirmed";
    public const string MaintenancePaymentFailed = "MaintenancePaymentFailed";
    public const string MaintenancePaymentResolved = "MaintenancePaymentResolved";
    public const string MaintenanceGracePeriodEnding = "MaintenanceGracePeriodEnding";
}
