using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Msm.Portfolio.Web.Authorization;
using Msm.Portfolio.Web.Configuration;
using Msm.Portfolio.Web.Domain.Entities;
using Msm.Portfolio.Web.ViewModels;

namespace Msm.Portfolio.Web.Controllers;

/// <summary>
/// Sign-in and sign-out (specification section 34). Password reset and the onboarding
/// flow are added in Phase 2.
/// </summary>
[Route("account")]
public class AccountController(
    SignInManager<ApplicationUser> signInManager,
    UserManager<ApplicationUser> userManager,
    ILogger<AccountController> logger) : Controller
{
    [HttpGet("login")]
    [AllowAnonymous]
    public IActionResult Login(string? returnUrl = null)
        => View(new LoginViewModel { ReturnUrl = returnUrl });

    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.SignIn)]
    public async Task<IActionResult> Login(LoginViewModel model)
    {
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        var user = await userManager.FindByEmailAsync(model.Email);

        // A disabled account must not be able to sign in, but the message stays generic:
        // distinguishing "no such user" from "disabled" would confirm which emails exist.
        if (user is null || !user.IsActive)
        {
            logger.LogInformation("Rejected sign-in for {Email}.", model.Email);
            ModelState.AddModelError(string.Empty, "Invalid email address or password.");
            return View(model);
        }

        var result = await signInManager.PasswordSignInAsync(
            user, model.Password, model.RememberMe, lockoutOnFailure: true);

        if (result.IsLockedOut)
        {
            ModelState.AddModelError(string.Empty,
                "This account is temporarily locked after too many failed attempts. Please try again later.");
            return View(model);
        }

        if (!result.Succeeded)
        {
            ModelState.AddModelError(string.Empty, "Invalid email address or password.");
            return View(model);
        }

        user.LastLoginAt = DateTimeOffset.UtcNow;
        await userManager.UpdateAsync(user);

        // Only redirect to a local path: an attacker-supplied absolute returnUrl would
        // bounce a freshly authenticated user to an external site.
        if (!string.IsNullOrWhiteSpace(model.ReturnUrl) && Url.IsLocalUrl(model.ReturnUrl))
        {
            return Redirect(model.ReturnUrl);
        }

        return Redirect(await LandingPageForAsync(user));
    }

    /// <summary>
    /// Changing your own password.
    /// </summary>
    /// <remarks>
    /// Both a model and a member of staff are given their first password by somebody else,
    /// who necessarily saw it. Until this existed there was no way to stop them knowing it
    /// — the screen that hands it over even says to change it afterwards, which was not
    /// true of anything the site could actually do.
    /// </remarks>
    [HttpGet("password")]
    [Authorize]
    public IActionResult Password() => View(new ChangePasswordViewModel());

    [HttpPost("password")]
    [Authorize]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting(RateLimitPolicies.SignIn)]
    public async Task<IActionResult> Password(ChangePasswordViewModel model)
    {
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        var user = await userManager.GetUserAsync(User);

        if (user is null)
        {
            return Challenge();
        }

        var result = await userManager.ChangePasswordAsync(
            user, model.CurrentPassword, model.NewPassword);

        if (!result.Succeeded)
        {
            foreach (var error in result.Errors)
            {
                ModelState.AddModelError(string.Empty, error.Description);
            }

            return View(model);
        }

        // The sign-in cookie carries a stamp of the account's security state. Refreshing it
        // keeps this browser signed in, and — more to the point — invalidates any other
        // session that was opened with the old password.
        await signInManager.RefreshSignInAsync(user);

        logger.LogInformation("Password changed for {UserId}.", user.Id);

        TempData["PasswordChanged"] = true;

        return RedirectToAction(nameof(Password));
    }

    [HttpPost("logout")]
    [Authorize]
    public async Task<IActionResult> Logout()
    {
        await signInManager.SignOutAsync();
        return RedirectToAction(nameof(Login));
    }

    [HttpGet("denied")]
    public IActionResult Denied() => View();

    /// <summary>
    /// Sends each role to the area it works in, so nobody lands on a page they cannot use.
    /// </summary>
    private async Task<string> LandingPageForAsync(ApplicationUser user)
    {
        var roles = await userManager.GetRolesAsync(user);

        if (roles.Contains(Roles.SuperAdmin) || roles.Contains(Roles.Admin))
        {
            return "/admin";
        }

        if (roles.Contains(Roles.Retoucher))
        {
            return "/retoucher";
        }

        return roles.Contains(Roles.Client) ? "/client" : "/";
    }
}
