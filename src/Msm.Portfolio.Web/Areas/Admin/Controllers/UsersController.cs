using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Msm.Portfolio.Web.Authorization;
using Msm.Portfolio.Web.Domain.Entities;
using Msm.Portfolio.Web.Services;
using Msm.Portfolio.Web.ViewModels;

namespace Msm.Portfolio.Web.Areas.Admin.Controllers;

/// <summary>
/// Staff accounts and role permissions (specification section 4).
/// </summary>
[Area("Admin")]
[Route("admin/users")]
[Authorize(Policy = Permissions.Users.ManageStaff)]
public class UsersController(
    IStaffService staff,
    UserManager<ApplicationUser> userManager) : Controller
{
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken = default)
    {
        return View(new StaffListViewModel
        {
            Staff = [.. await staff.GetStaffAsync(cancellationToken)],
            AdminPermissions = [.. await staff.GetPermissionsAsync(Roles.Admin, cancellationToken)],
            RetoucherPermissions = [.. await staff.GetPermissionsAsync(Roles.Retoucher, cancellationToken)],
            AllPermissions = [.. Permissions.All().Order()],
            SuperAdminOnly = [.. Permissions.SuperAdminOnly]
        });
    }

    /// <summary>Creating staff accounts is reserved to the Super Admin.</summary>
    [HttpPost("create")]
    [Authorize(Policy = Permissions.Users.ManageAdministrators)]
    public async Task<IActionResult> Create(
        CreateStaffViewModel model, CancellationToken cancellationToken = default)
    {
        if (!ModelState.IsValid)
        {
            TempData["Error"] = "Please check the details and try again.";
            return RedirectToAction(nameof(Index));
        }

        var result = await staff.CreateAsync(
            model.Email, model.FirstName, model.LastName, model.Role, CurrentUserId(), cancellationToken);

        if (!result.Succeeded)
        {
            TempData["Error"] = result.Error;
            return RedirectToAction(nameof(Index));
        }

        // Shown once. It is not stored in readable form, so it cannot be shown again.
        TempData["GeneratedPassword"] = result.GeneratedPassword;
        TempData["GeneratedFor"] = model.Email;

        return RedirectToAction(nameof(Index));
    }

    [HttpPost("{userId:guid}/active")]
    [Authorize(Policy = Permissions.Users.ManageAdministrators)]
    public async Task<IActionResult> SetActive(
        Guid userId, bool active, CancellationToken cancellationToken = default)
    {
        var result = await staff.SetActiveAsync(userId, active, CurrentUserId(), cancellationToken);

        if (!result.Succeeded)
        {
            TempData["Error"] = result.Error;
        }

        return RedirectToAction(nameof(Index));
    }

    [HttpPost("{userId:guid}/reset-password")]
    [Authorize(Policy = Permissions.Users.ManageAdministrators)]
    public async Task<IActionResult> ResetPassword(
        Guid userId, CancellationToken cancellationToken = default)
    {
        var (succeeded, password, error) = await staff.ResetPasswordAsync(
            userId, CurrentUserId(), cancellationToken);

        if (!succeeded)
        {
            TempData["Error"] = error;
            return RedirectToAction(nameof(Index));
        }

        TempData["GeneratedPassword"] = password;
        TempData["GeneratedFor"] = (await userManager.FindByIdAsync(userId.ToString()))?.Email;

        return RedirectToAction(nameof(Index));
    }

    [HttpPost("permissions")]
    [Authorize(Policy = Permissions.Users.ManageAdministrators)]
    public async Task<IActionResult> SetPermissions(
        string role, List<string> permissions, CancellationToken cancellationToken = default)
    {
        var result = await staff.SetPermissionsAsync(
            role, permissions ?? [], CurrentUserId(), cancellationToken);

        if (result.Succeeded)
        {
            TempData["Saved"] = $"Permissions updated for {role}. Affected staff see the change when they next sign in.";
        }
        else
        {
            TempData["Error"] = result.Error;
        }

        return RedirectToAction(nameof(Index));
    }

    private Guid? CurrentUserId() =>
        Guid.TryParse(userManager.GetUserId(User), out var id) ? id : null;
}
