using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Msm.Portfolio.Web.Authorization;

namespace Msm.Portfolio.Tests;

/// <summary>
/// The permission handler decides every privileged action, so its behaviour is
/// pinned down directly rather than only through the areas that consume it.
/// </summary>
public class PermissionAuthorizationHandlerTests
{
    private static async Task<bool> EvaluateAsync(ClaimsPrincipal user, string permission)
    {
        var requirement = new PermissionRequirement(permission);
        var context = new AuthorizationHandlerContext([requirement], user, resource: null);

        await new PermissionAuthorizationHandler().HandleAsync(context);

        return context.HasSucceeded;
    }

    private static ClaimsPrincipal UserWith(string? role = null, params string[] permissions)
    {
        var claims = new List<Claim>();

        if (role is not null)
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        claims.AddRange(permissions.Select(p => new Claim(Permissions.ClaimType, p)));

        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Test"));
    }

    [Fact]
    public async Task Grants_when_the_user_holds_the_permission_claim()
    {
        var user = UserWith(Roles.Admin, Permissions.Portfolios.Publish);

        Assert.True(await EvaluateAsync(user, Permissions.Portfolios.Publish));
    }

    [Fact]
    public async Task Denies_when_the_user_lacks_the_permission_claim()
    {
        var user = UserWith(Roles.Admin, Permissions.Portfolios.Publish);

        Assert.False(await EvaluateAsync(user, Permissions.Portfolios.DeletePermanently));
    }

    [Fact]
    public async Task Denies_an_anonymous_user()
    {
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());

        Assert.False(await EvaluateAsync(anonymous, Permissions.Portfolios.View));
    }

    /// <summary>
    /// Specification section 4 gives the Super Admin unrestricted access. Asserting it
    /// across every permission means a permission added later cannot silently fall
    /// outside the owner's reach.
    /// </summary>
    [Fact]
    public async Task Super_admin_holds_every_permission_without_any_claim()
    {
        var superAdmin = UserWith(Roles.SuperAdmin);

        foreach (var permission in Permissions.All())
        {
            Assert.True(
                await EvaluateAsync(superAdmin, permission),
                $"Super Admin was denied '{permission}'.");
        }
    }

    /// <summary>
    /// The reserved capabilities in specification section 4 must never appear in
    /// another role's defaults. This guards the seeding table against a careless edit.
    /// </summary>
    [Fact]
    public void Super_admin_only_permissions_are_not_granted_to_any_other_role()
    {
        foreach (var (role, granted) in Permissions.DefaultsByRole)
        {
            var reserved = granted.Intersect(Permissions.SuperAdminOnly).ToArray();

            Assert.True(
                reserved.Length == 0,
                $"Role '{role}' was granted Super Admin-only permission(s): {string.Join(", ", reserved)}.");
        }
    }

    [Fact]
    public void Every_default_granted_permission_is_a_known_permission()
    {
        var known = Permissions.All().ToHashSet(StringComparer.Ordinal);

        foreach (var (role, granted) in Permissions.DefaultsByRole)
        {
            foreach (var permission in granted)
            {
                Assert.True(
                    known.Contains(permission),
                    $"Role '{role}' grants '{permission}', which is not registered in Permissions.All() "
                    + "and would therefore have no policy.");
            }
        }
    }

    /// <summary>
    /// A retoucher must not be able to publish, unpublish or delete
    /// (specification section 6).
    /// </summary>
    [Theory]
    [InlineData(Permissions.Portfolios.Publish)]
    [InlineData(Permissions.Portfolios.Unpublish)]
    [InlineData(Permissions.Portfolios.DeletePermanently)]
    [InlineData(Permissions.Users.ManageStaff)]
    [InlineData(Permissions.Payments.Override)]
    public async Task Retoucher_is_denied_capabilities_reserved_to_staff_above_them(string permission)
    {
        var retoucher = UserWith(Roles.Retoucher, Permissions.DefaultsByRole[Roles.Retoucher]);

        Assert.False(await EvaluateAsync(retoucher, permission));
    }
}
