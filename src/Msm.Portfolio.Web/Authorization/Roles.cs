namespace Msm.Portfolio.Web.Authorization;

/// <summary>
/// The five authenticated roles. There is deliberately no Agency role: agencies and
/// other viewers open the public portfolio URL without signing in at all.
/// </summary>
public static class Roles
{
    public const string SuperAdmin = "SuperAdmin";
    public const string Admin = "Admin";
    public const string Retoucher = "Retoucher";

    /// <summary>
    /// A restricted staff role: can see the clients and portfolios their permissions
    /// allow, with no editing capability granted by default. Uses the same client list
    /// and detail pages as Admin — the permission claims a Viewer holds are what narrow
    /// what they can do there, not a separate set of screens.
    /// </summary>
    public const string Viewer = "Viewer";

    public const string Client = "Client";

    public static readonly IReadOnlyList<string> All = new[] { SuperAdmin, Admin, Retoucher, Viewer, Client };

    /// <summary>Roles that make up MSM staff, as opposed to the models themselves.</summary>
    public static readonly IReadOnlyList<string> Staff = new[] { SuperAdmin, Admin, Retoucher, Viewer };
}
