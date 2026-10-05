using System.ComponentModel.DataAnnotations;
using Msm.Portfolio.Web.Services;

namespace Msm.Portfolio.Web.ViewModels;

/// <summary>Staff accounts and role permissions (specification section 4).</summary>
public class StaffListViewModel
{
    public List<StaffMember> Staff { get; set; } = [];

    public List<string> AdminPermissions { get; set; } = [];

    public List<string> RetoucherPermissions { get; set; } = [];

    public List<string> AllPermissions { get; set; } = [];

    /// <summary>
    /// Capabilities reserved to the Super Admin. Shown but not offerable, so it is
    /// clear they exist and equally clear they cannot be delegated.
    /// </summary>
    public List<string> SuperAdminOnly { get; set; } = [];

    public bool IsReserved(string permission) => SuperAdminOnly.Contains(permission);

    public List<string> GrantedTo(string role) =>
        role == Authorization.Roles.Admin ? AdminPermissions : RetoucherPermissions;

    /// <summary>Turns "portfolio.delete.permanent" into "Portfolio delete permanent".</summary>
    public static string Humanise(string permission)
    {
        var words = permission.Replace('.', ' ');
        return char.ToUpperInvariant(words[0]) + words[1..];
    }
}

public class CreateStaffViewModel
{
    [Required]
    [EmailAddress]
    [Display(Name = "Email address")]
    public string Email { get; set; } = string.Empty;

    [Required]
    [StringLength(100)]
    [Display(Name = "First name")]
    public string FirstName { get; set; } = string.Empty;

    [Required]
    [StringLength(100)]
    [Display(Name = "Last name")]
    public string LastName { get; set; } = string.Empty;

    [Required]
    public string Role { get; set; } = Authorization.Roles.Retoucher;
}

/// <summary>The audit history (specification sections 4 and 36).</summary>
public class AuditViewModel
{
    public List<AuditEntry> Entries { get; set; } = [];

    public string? EntityType { get; set; }

    public string? Action { get; set; }

    public List<string> EntityTypes { get; set; } = [];

    public List<string> Actions { get; set; } = [];

    public int Page { get; set; }

    public int PageSize { get; set; }

    public bool HasMore { get; set; }
}

public record AuditEntry(
    DateTimeOffset Timestamp,
    string? User,
    string EntityType,
    string EntityId,
    string Action,
    string? OldValue,
    string? NewValue);
