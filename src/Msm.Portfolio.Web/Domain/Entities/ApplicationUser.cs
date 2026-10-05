using Microsoft.AspNetCore.Identity;

namespace Msm.Portfolio.Web.Domain.Entities;

/// <summary>
/// Authentication record for every human who signs in: Super Admin, Admin,
/// Retoucher or Client (specification sections 3 and 26). Role membership lives in
/// Identity's role tables rather than a flag here, so a user's capabilities are
/// resolved through role and policy authorization.
/// </summary>
public class ApplicationUser : IdentityUser<Guid>
{
    public string? FirstName { get; set; }

    public string? LastName { get; set; }

    /// <summary>
    /// Disables sign-in without deleting the account, so a departed staff member's
    /// past work stays attributable in the audit log.
    /// </summary>
    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? LastLoginAt { get; set; }

    /// <summary>Set only for users in the Client role; null for staff accounts.</summary>
    public ClientProfile? ClientProfile { get; set; }

    public string DisplayName =>
        string.IsNullOrWhiteSpace(FirstName) && string.IsNullOrWhiteSpace(LastName)
            ? Email ?? UserName ?? Id.ToString()
            : $"{FirstName} {LastName}".Trim();
}

/// <summary>
/// Application role. Named roles are defined in <see cref="Authorization.Roles"/>;
/// fine-grained capabilities are granted as permission claims on the role so that
/// two Admins can hold different privileges (specification section 5).
/// </summary>
public class ApplicationRole : IdentityRole<Guid>
{
    public ApplicationRole() { }

    public ApplicationRole(string roleName) : base(roleName) { }

    public string? Description { get; set; }
}
