using Microsoft.EntityFrameworkCore;
using Msm.Portfolio.Web.Data;
using Msm.Portfolio.Web.Domain.Enums;

namespace Msm.Portfolio.Web.Services;

/// <summary>One row of the admin client table (specification section 5).</summary>
public record AdminClientRow(
    Guid ClientId,
    string ClientName,
    string? Email,
    PortfolioStatus Status,
    string? Retoucher,
    int ImageCount,
    int SelectedCount,
    DateTimeOffset SubmittedAt,
    PaymentStatus? PaymentStatus,
    bool IsPublished,
    string? Slug,
    bool GuardianApprovalPending);

/// <summary>The filters above the table (specification section 5).</summary>
public record AdminClientFilter(
    string? Search = null,
    PortfolioStatus? Status = null,
    Guid? RetoucherUserId = null);

public record StaffOption(Guid UserId, string Name);

public interface IAdminService
{
    Task<IReadOnlyList<AdminClientRow>> SearchAsync(
        AdminClientFilter filter, CancellationToken cancellationToken = default);

    /// <summary>Retouchers, for the "filter by retoucher" control.</summary>
    Task<IReadOnlyList<StaffOption>> GetRetouchersAsync(CancellationToken cancellationToken = default);
}

public class AdminService(ApplicationDbContext db) : IAdminService
{
    public async Task<IReadOnlyList<AdminClientRow>> SearchAsync(
        AdminClientFilter filter, CancellationToken cancellationToken = default)
    {
        var query =
            from client in db.ClientProfiles
            join user in db.Users on client.ApplicationUserId equals user.Id
            join portfolio in db.Portfolios on client.Id equals portfolio.ClientId into portfolios
            from portfolio in portfolios.DefaultIfEmpty()
            select new
            {
                client.Id,
                client.FirstName,
                client.LastName,
                client.DisplayName,
                client.DateOfBirth,
                client.CreatedAt,
                user.Email,
                Status = portfolio == null ? PortfolioStatus.AwaitingClientInformation : portfolio.Status,
                IsPublished = portfolio != null && portfolio.IsPublished,
                Slug = portfolio == null ? null : portfolio.Slug,
                GuardianStatus = client.GuardianConsent == null
                    ? (GuardianConsentStatus?)null
                    : client.GuardianConsent.Status,
                ImageCount = db.MediaAssets.Count(m =>
                    m.ClientId == client.Id && !m.IsDeleted && m.MediaType == MediaType.Image),
                SelectedCount = db.MediaAssets.Count(m =>
                    m.ClientId == client.Id && !m.IsDeleted
                    && m.MediaType == MediaType.Image && m.IsSelectedForPortfolio),
                Assignment = db.RetoucherAssignments
                    .Where(a => a.ClientId == client.Id)
                    .OrderByDescending(a => a.AssignedAt)
                    .Select(a => new { a.RetoucherUserId, a.RetoucherUser.FirstName, a.RetoucherUser.LastName })
                    .FirstOrDefault(),
                Payment = db.Orders
                    .Where(o => o.ClientId == client.Id)
                    .OrderByDescending(o => o.CreatedAt)
                    .SelectMany(o => o.Transactions)
                    .OrderByDescending(t => t.CreatedAt)
                    .Select(t => (PaymentStatus?)t.Status)
                    .FirstOrDefault()
            };

        if (filter.Status is { } status)
        {
            query = query.Where(r => r.Status == status);
        }

        if (filter.RetoucherUserId is { } retoucherId)
        {
            query = query.Where(r => r.Assignment != null && r.Assignment.RetoucherUserId == retoucherId);
        }

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var term = filter.Search.Trim();

            // Matched against the name the client is known by and their email, which is
            // what staff have to hand when someone telephones the studio.
            query = query.Where(r =>
                EF.Functions.Like(r.FirstName, $"%{term}%")
                || EF.Functions.Like(r.LastName, $"%{term}%")
                || (r.DisplayName != null && EF.Functions.Like(r.DisplayName, $"%{term}%"))
                || (r.Email != null && EF.Functions.Like(r.Email, $"%{term}%")));
        }

        var rows = await query.ToListAsync(cancellationToken);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        return
        [
            .. rows
                .OrderByDescending(r => r.CreatedAt)
                .Select(r => new AdminClientRow(
                    r.Id,
                    string.IsNullOrWhiteSpace(r.DisplayName) ? $"{r.FirstName} {r.LastName}".Trim() : r.DisplayName,
                    r.Email,
                    r.Status,
                    r.Assignment is null ? null : $"{r.Assignment.FirstName} {r.Assignment.LastName}".Trim(),
                    r.ImageCount,
                    r.SelectedCount,
                    r.CreatedAt,
                    r.Payment,
                    r.IsPublished,
                    r.Slug,
                    new Domain.Entities.ClientProfile { DateOfBirth = r.DateOfBirth }.RequiresGuardianConsent(today)
                        && r.GuardianStatus != GuardianConsentStatus.Approved))
        ];
    }

    public async Task<IReadOnlyList<StaffOption>> GetRetouchersAsync(
        CancellationToken cancellationToken = default)
    {
        var rows = await (
            from user in db.Users
            join userRole in db.UserRoles on user.Id equals userRole.UserId
            join role in db.Roles on userRole.RoleId equals role.Id
            where role.Name == Authorization.Roles.Retoucher && user.IsActive
            select new { user.Id, user.FirstName, user.LastName, user.Email })
            .ToListAsync(cancellationToken);

        return
        [
            .. rows
                .Select(r => new StaffOption(
                    r.Id,
                    string.IsNullOrWhiteSpace($"{r.FirstName}{r.LastName}")
                        ? r.Email ?? r.Id.ToString()
                        : $"{r.FirstName} {r.LastName}".Trim()))
                .OrderBy(o => o.Name)
        ];
    }
}
