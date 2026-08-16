using Microsoft.AspNetCore.Authorization;

namespace Msm.Portfolio.Web.Authorization;

/// <summary>Named policies for the role-scoped areas of the application.</summary>
public static class Policies
{
    /// <summary>Any MSM staff member: Super Admin, Admin or Retoucher.</summary>
    public const string StaffOnly = "StaffOnly";

    /// <summary>Admin area access: Admin or Super Admin.</summary>
    public const string AdminArea = "AdminArea";

    /// <summary>Retoucher area access. Admins are included so they can see the queue.</summary>
    public const string RetoucherArea = "RetoucherArea";

    /// <summary>Client area access: the models themselves.</summary>
    public const string ClientArea = "ClientArea";

    /// <summary>Reserved for the system owner.</summary>
    public const string SuperAdminOnly = "SuperAdminOnly";
}

public static class AuthorizationRegistration
{
    /// <summary>
    /// Registers one policy per permission plus the area policies. Permission policies
    /// are named after the permission itself, so a controller reads
    /// <c>[Authorize(Policy = Permissions.Portfolios.Publish)]</c>.
    /// </summary>
    public static IServiceCollection AddMsmAuthorization(this IServiceCollection services)
    {
        services.AddSingleton<IAuthorizationHandler, PermissionAuthorizationHandler>();

        services.AddAuthorizationBuilder()
            .AddPolicy(Policies.SuperAdminOnly, policy => policy.RequireRole(Roles.SuperAdmin))
            .AddPolicy(Policies.StaffOnly, policy => policy.RequireRole(Roles.Staff.ToArray()))
            .AddPolicy(Policies.AdminArea, policy => policy.RequireRole(Roles.SuperAdmin, Roles.Admin))
            .AddPolicy(Policies.RetoucherArea, policy => policy.RequireRole(Roles.SuperAdmin, Roles.Admin, Roles.Retoucher))
            .AddPolicy(Policies.ClientArea, policy => policy.RequireRole(Roles.Client));

        services.AddAuthorization(options =>
        {
            foreach (var permission in Permissions.All())
            {
                options.AddPolicy(permission, policy =>
                {
                    policy.RequireAuthenticatedUser();
                    policy.Requirements.Add(new PermissionRequirement(permission));
                });
            }
        });

        return services;
    }
}
