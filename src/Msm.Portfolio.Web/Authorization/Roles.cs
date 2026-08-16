namespace Msm.Portfolio.Web.Authorization;

/// <summary>
/// The four authenticated roles (specification section 3). There is deliberately no
/// Agency role: agencies and other viewers open the public portfolio URL without
/// signing in at all.
/// </summary>
public static class Roles
{
    public const string SuperAdmin = "SuperAdmin";
    public const string Admin = "Admin";
    public const string Retoucher = "Retoucher";
    public const string Client = "Client";

    public static readonly IReadOnlyList<string> All = new[] { SuperAdmin, Admin, Retoucher, Client };

    /// <summary>Roles that make up MSM staff, as opposed to the models themselves.</summary>
    public static readonly IReadOnlyList<string> Staff = new[] { SuperAdmin, Admin, Retoucher };
}
