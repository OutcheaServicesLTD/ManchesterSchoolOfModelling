using Microsoft.AspNetCore.Authorization;

namespace Msm.Portfolio.Web.Authorization;

/// <summary>Requires a named permission claim (specification section 35).</summary>
public class PermissionRequirement(string permission) : IAuthorizationRequirement
{
    public string Permission { get; } = permission;
}

/// <summary>
/// Grants access when the signed-in user holds the required permission claim.
/// </summary>
/// <remarks>
/// Super Admin succeeds unconditionally, which is what gives that role the
/// unrestricted access described in specification section 4. Doing it here rather than
/// by seeding every permission as a claim means a permission added later is covered
/// automatically instead of silently missing from the Super Admin's claim list.
/// </remarks>
public class PermissionAuthorizationHandler : AuthorizationHandler<PermissionRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        PermissionRequirement requirement)
    {
        if (context.User.IsInRole(Roles.SuperAdmin))
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        var hasPermission = context.User.HasClaim(
            claim => claim.Type == Permissions.ClaimType
                     && string.Equals(claim.Value, requirement.Permission, StringComparison.Ordinal));

        if (hasPermission)
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
