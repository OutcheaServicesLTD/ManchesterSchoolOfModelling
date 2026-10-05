using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Msm.Portfolio.Web.Authorization;
using Msm.Portfolio.Web.Data;
using Msm.Portfolio.Web.ViewModels;

namespace Msm.Portfolio.Web.Areas.Admin.Controllers;

/// <summary>
/// The system audit history (specification sections 4 and 36).
/// </summary>
[Area("Admin")]
[Route("admin/audit")]
[Authorize(Policy = Permissions.System.ViewAudit)]
public class AuditController(ApplicationDbContext db) : Controller
{
    private const int PageSize = 50;

    /// <summary>
    /// Lists audit entries, newest first.
    /// </summary>
    /// <remarks>
    /// The action filter binds explicitly from the query string and is named
    /// <c>auditAction</c> in code. "action" is a reserved MVC route value holding the
    /// action method's own name, so a parameter called <c>action</c> silently receives
    /// "Index" and filters every row away.
    /// </remarks>
    [HttpGet("")]
    public async Task<IActionResult> Index(
        [FromQuery] string? entityType = null,
        [FromQuery(Name = "auditAction")] string? auditAction = null,
        [FromQuery] int page = 0,
        CancellationToken cancellationToken = default)
    {
        var query = db.AuditLogs.AsQueryable();

        if (!string.IsNullOrWhiteSpace(entityType))
        {
            query = query.Where(a => a.EntityType == entityType);
        }

        if (!string.IsNullOrWhiteSpace(auditAction))
        {
            query = query.Where(a => a.Action == auditAction);
        }

        page = Math.Max(0, page);

        // One row beyond the page, so "is there more" needs no second count query.
        var rows = await query
            .OrderByDescending(a => a.Timestamp)
            .Skip(page * PageSize)
            .Take(PageSize + 1)
            .Select(a => new
            {
                a.Timestamp,
                a.EntityType,
                a.EntityId,
                a.Action,
                a.OldValue,
                a.NewValue,
                UserName = a.User == null
                    ? null
                    : (a.User.FirstName ?? "") + " " + (a.User.LastName ?? "")
            })
            .ToListAsync(cancellationToken);

        var hasMore = rows.Count > PageSize;

        return View(new AuditViewModel
        {
            EntityType = entityType,
            Action = auditAction,
            Page = page,
            PageSize = PageSize,
            HasMore = hasMore,
            Entries =
            [
                .. rows.Take(PageSize).Select(r => new AuditEntry(
                    r.Timestamp,
                    // Null for system-initiated actions such as webhook processing.
                    string.IsNullOrWhiteSpace(r.UserName) ? null : r.UserName.Trim(),
                    r.EntityType,
                    r.EntityId,
                    r.Action,
                    r.OldValue,
                    r.NewValue))
            ],
            EntityTypes = await db.AuditLogs
                .Select(a => a.EntityType).Distinct().OrderBy(t => t).ToListAsync(cancellationToken),
            Actions = await db.AuditLogs
                .Select(a => a.Action).Distinct().OrderBy(t => t).ToListAsync(cancellationToken)
        });
    }
}
