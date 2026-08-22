using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Msm.Portfolio.Web.Areas.Admin.Controllers;
using Msm.Portfolio.Web.Authorization;
using Msm.Portfolio.Web.ViewModels;

namespace Msm.Portfolio.Tests;

/// <summary>
/// Seeing a model's dashboard used to mean issuing them a password and signing in as them,
/// which resets the password of anybody already using theirs and puts a staff member inside
/// a real person's account. The preview replaces that, and must stay a preview.
/// </summary>
public class ClientPortalPreviewTests
{
    private static MethodInfo Portal =>
        typeof(ClientsController).GetMethod(nameof(ClientsController.Portal))!;

    /// <summary>
    /// A GET and nothing else. A preview that accepted a POST would be a way to act as
    /// somebody, which is the thing this exists to avoid.
    /// </summary>
    [Fact]
    public void The_preview_only_reads()
    {
        var verbs = Portal.GetCustomAttributes<Microsoft.AspNetCore.Mvc.HttpGetAttribute>().ToList();

        Assert.Single(verbs);
        Assert.Equal("portal", verbs[0].Template);
        Assert.Empty(Portal.GetCustomAttributes<Microsoft.AspNetCore.Mvc.HttpPostAttribute>());
    }

    /// <summary>
    /// Behind the same permission as seeing the client record itself. A retoucher can open
    /// the workspace but not this.
    /// </summary>
    [Fact]
    public void The_preview_needs_permission_to_see_clients()
    {
        var policy = Portal.GetCustomAttribute<AuthorizeAttribute>();

        Assert.NotNull(policy);
        Assert.Equal(Permissions.Clients.ViewAll, policy!.Policy);
    }

    /// <summary>
    /// The flag exists to stand down the model's own controls, not to change what is shown.
    /// A dashboard is a preview or it is not; nothing else about it moves.
    /// </summary>
    [Fact]
    public void A_preview_is_marked_as_one()
    {
        Assert.False(new ClientDashboardViewModel().IsPreview);
        Assert.True(new ClientDashboardViewModel { IsPreview = true }.IsPreview);
    }

    /// <summary>
    /// One view between the model's page and the preview. Two copies would drift, and a
    /// preview that has drifted is worse than none because it is believed.
    /// </summary>
    [Fact]
    public void Both_pages_render_the_same_dashboard()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MsmPortfolio.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        var web = Path.Combine(directory!.FullName, "src", "Msm.Portfolio.Web");

        var partial = Path.Combine(web, "Areas", "Client", "Views", "Home", "_Dashboard.cshtml");
        Assert.True(File.Exists(partial), $"Expected the shared dashboard at {partial}.");

        foreach (var page in new[]
                 {
                     Path.Combine(web, "Areas", "Client", "Views", "Home", "Index.cshtml"),
                     Path.Combine(web, "Areas", "Admin", "Views", "Clients", "Portal.cshtml")
                 })
        {
            Assert.Contains("_Dashboard.cshtml\"", File.ReadAllText(page).Replace("\"_Dashboard\"", "_Dashboard.cshtml\""));
        }
    }
}
