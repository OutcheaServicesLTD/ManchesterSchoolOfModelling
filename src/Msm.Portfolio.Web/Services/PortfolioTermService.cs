using Microsoft.EntityFrameworkCore;
using Msm.Portfolio.Web.Data;

namespace Msm.Portfolio.Web.Services;

/// <summary>
/// The purchased year running out.
/// </summary>
/// <remarks>
/// £99 buys the portfolio for a year and there is nothing else to pay, so what ends the
/// portfolio is the calendar rather than a failed collection. Nothing in a request can be
/// relied on to notice a date passing, so this is driven from a timer.
/// </remarks>
public interface IPortfolioTermService
{
    /// <summary>
    /// Takes down portfolios whose year is up. Returns how many were unpublished.
    /// </summary>
    Task<int> ExpireElapsedTermsAsync(CancellationToken cancellationToken = default);
}

public class PortfolioTermService(
    ApplicationDbContext db,
    IPortfolioService portfolios,
    IAuditService audit,
    INotificationService notifications,
    ILogger<PortfolioTermService> logger) : IPortfolioTermService
{
    public async Task<int> ExpireElapsedTermsAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;

        // Published ones only. A portfolio that is already down has nothing to take down,
        // and matching it would raise the same notice every hour for ever.
        var due = await db.Portfolios
            .Include(p => p.Client)
            .Where(p => p.IsPublished && p.ExpiresAt != null && p.ExpiresAt <= now)
            .ToListAsync(cancellationToken);

        var unpublished = 0;

        foreach (var portfolio in due)
        {
            audit.Record(nameof(Domain.Entities.Portfolio), portfolio.Id.ToString(),
                AuditActions.PortfolioTermExpired,
                newValue: $"Term ended {portfolio.ExpiresAt:d MMMM yyyy}");

            await db.SaveChangesAsync(cancellationToken);

            // Unpublishing also removes the Model Board listing, because the board is
            // queried from published portfolios (specification section 47).
            var result = await portfolios.UnpublishAsync(
                portfolio.ClientId, null, "the purchased year has ended", cancellationToken);

            if (result.Succeeded)
            {
                unpublished++;

                await notifications.NotifyStaffAsync(
                    NotificationTypes.PortfolioUnpublished,
                    $"{portfolio.Client.PublicName}'s year has ended and their portfolio was taken down.",
                    $"/admin/clients/{portfolio.ClientId}",
                    cancellationToken);

                notifications.NotifyUser(
                    portfolio.Client.ApplicationUserId,
                    NotificationTypes.PortfolioUnpublished,
                    "Your year has ended and your portfolio is no longer public.",
                    "/client");

                await db.SaveChangesAsync(cancellationToken);
            }

            logger.LogInformation(
                "Portfolio term expired for client {ClientId}; unpublished: {Result}.",
                portfolio.ClientId, result.Succeeded);
        }

        return unpublished;
    }
}
