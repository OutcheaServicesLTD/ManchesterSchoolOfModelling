using System.ComponentModel.DataAnnotations;

namespace Msm.Portfolio.Web.ViewModels;

/// <summary>
/// Changing your own password (specification section 34).
/// </summary>
/// <remarks>
/// The current one is asked for as well as the new one. Without it, an unattended signed-in
/// browser is enough to lock the owner out of their own account.
/// </remarks>
public class ChangePasswordViewModel
{
    [Required]
    [DataType(DataType.Password)]
    [Display(Name = "Your current password")]
    public string CurrentPassword { get; set; } = string.Empty;

    [Required]
    [DataType(DataType.Password)]
    [Display(Name = "New password")]
    public string NewPassword { get; set; } = string.Empty;

    [Required]
    [DataType(DataType.Password)]
    [Display(Name = "New password again")]
    [Compare(nameof(NewPassword), ErrorMessage = "The two passwords do not match.")]
    public string ConfirmPassword { get; set; } = string.Empty;
}
