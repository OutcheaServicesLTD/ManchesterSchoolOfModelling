using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Msm.Portfolio.Web.Authorization;
using Msm.Portfolio.Web.Data;
using Msm.Portfolio.Web.Domain.Entities;
using Msm.Portfolio.Web.Services;

namespace Msm.Portfolio.Tests;

/// <summary>
/// Staff management is the Super Admin's, so these tests focus on what must not be
/// possible: delegating reserved capabilities, or locking everyone out.
/// </summary>
public class StaffServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _provider;
    private readonly IServiceScope _scope;
    private readonly ApplicationDbContext _db;
    private readonly StaffService _service;
    private readonly RoleManager<ApplicationRole> _roleManager;

    public StaffServiceTests()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddLogging();

        // Password reset tokens are data-protected, so the token providers cannot be
        // constructed without this.
        services.AddDataProtection();

        services.AddDbContext<ApplicationDbContext>(o => o.UseSqlite(_connection));
        services.AddIdentityCore<ApplicationUser>(o =>
            {
                o.Password.RequiredLength = 10;
                o.Password.RequireNonAlphanumeric = true;
                o.User.RequireUniqueEmail = true;
            })
            .AddRoles<ApplicationRole>()
            .AddEntityFrameworkStores<ApplicationDbContext>()
            .AddDefaultTokenProviders();

        _provider = services.BuildServiceProvider();
        _scope = _provider.CreateScope();

        _db = _scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        _db.Database.EnsureCreated();

        var userManager = _scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        _roleManager = _scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();

        foreach (var role in Roles.All)
        {
            _roleManager.CreateAsync(new ApplicationRole(role)).GetAwaiter().GetResult();
        }

        _service = new StaffService(
            _db, userManager, _roleManager, new AuditService(_db), NullLogger<StaffService>.Instance);
    }

    public void Dispose()
    {
        _scope.Dispose();
        _provider.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task A_retoucher_account_can_be_created_with_a_one_time_password()
    {
        var result = await _service.CreateAsync("new@msm.local", "New", "Person", Roles.Retoucher, null);

        Assert.True(result.Succeeded);
        Assert.False(string.IsNullOrWhiteSpace(result.GeneratedPassword));

        var staff = await _service.GetStaffAsync();
        Assert.Contains(staff, s => s.Email == "new@msm.local" && s.Role == Roles.Retoucher);
    }

    [Fact]
    public async Task The_generated_password_meets_the_configured_complexity_rules()
    {
        var result = await _service.CreateAsync("complex@msm.local", "A", "B", Roles.Admin, null);
        var password = result.GeneratedPassword!;

        Assert.True(password.Length >= 14);
        Assert.Contains(password, char.IsUpper);
        Assert.Contains(password, char.IsLower);
        Assert.Contains(password, char.IsDigit);
        Assert.Contains(password, c => !char.IsLetterOrDigit(c));
    }

    [Fact]
    public async Task Two_created_accounts_do_not_share_a_password()
    {
        var first = await _service.CreateAsync("one@msm.local", "A", "B", Roles.Retoucher, null);
        var second = await _service.CreateAsync("two@msm.local", "C", "D", Roles.Retoucher, null);

        Assert.NotEqual(first.GeneratedPassword, second.GeneratedPassword);
    }

    [Fact]
    public async Task A_duplicate_email_is_refused()
    {
        await _service.CreateAsync("dupe@msm.local", "A", "B", Roles.Retoucher, null);

        var second = await _service.CreateAsync("dupe@msm.local", "C", "D", Roles.Admin, null);

        Assert.False(second.Succeeded);
        Assert.Contains("already exists", second.Error);
    }

    /// <summary>
    /// Creating a Client here would produce an account with no profile, and a Super
    /// Admin is never created through the UI.
    /// </summary>
    [Theory]
    [InlineData(Roles.Client)]
    [InlineData(Roles.SuperAdmin)]
    [InlineData("Nonsense")]
    public async Task Only_admin_and_retoucher_accounts_can_be_created(string role)
    {
        var result = await _service.CreateAsync("x@msm.local", "A", "B", role, null);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task An_account_can_be_disabled_and_enabled()
    {
        var created = await _service.CreateAsync("toggle@msm.local", "A", "B", Roles.Retoucher, null);
        Assert.True(created.Succeeded);

        var userId = (await _service.GetStaffAsync()).Single(s => s.Email == "toggle@msm.local").UserId;

        Assert.True((await _service.SetActiveAsync(userId, false, null)).Succeeded);
        Assert.False((await _service.GetStaffAsync()).Single(s => s.UserId == userId).IsActive);

        Assert.True((await _service.SetActiveAsync(userId, true, null)).Succeeded);
        Assert.True((await _service.GetStaffAsync()).Single(s => s.UserId == userId).IsActive);
    }

    /// <summary>
    /// Disabling your own account would leave nobody able to manage staff.
    /// </summary>
    [Fact]
    public async Task You_cannot_disable_your_own_account()
    {
        await _service.CreateAsync("self@msm.local", "A", "B", Roles.Admin, null);
        var userId = (await _service.GetStaffAsync()).Single(s => s.Email == "self@msm.local").UserId;

        var result = await _service.SetActiveAsync(userId, false, actingUserId: userId);

        Assert.False(result.Succeeded);
        Assert.Contains("your own account", result.Error);
    }

    [Fact]
    public async Task Resetting_access_issues_a_new_password()
    {
        var created = await _service.CreateAsync("reset@msm.local", "A", "B", Roles.Retoucher, null);
        var userId = (await _service.GetStaffAsync()).Single(s => s.Email == "reset@msm.local").UserId;

        var (succeeded, password, _) = await _service.ResetPasswordAsync(userId, null);

        Assert.True(succeeded);
        Assert.False(string.IsNullOrWhiteSpace(password));
        Assert.NotEqual(created.GeneratedPassword, password);
    }

    [Fact]
    public async Task Permissions_can_be_granted_and_revoked_for_a_role()
    {
        await _service.SetPermissionsAsync(
            Roles.Retoucher,
            [Permissions.Media.Upload, Permissions.Portfolios.View],
            null);

        var granted = await _service.GetPermissionsAsync(Roles.Retoucher);
        Assert.Equal(2, granted.Count);
        Assert.Contains(Permissions.Media.Upload, granted);

        await _service.SetPermissionsAsync(Roles.Retoucher, [Permissions.Media.Upload], null);

        granted = await _service.GetPermissionsAsync(Roles.Retoucher);
        Assert.Single(granted);
        Assert.DoesNotContain(Permissions.Portfolios.View, granted);
    }

    /// <summary>
    /// The reserved capabilities in specification section 4 must not be delegable.
    /// A tampered form posting them is stripped rather than honoured.
    /// </summary>
    [Theory]
    [InlineData(Roles.Admin)]
    [InlineData(Roles.Retoucher)]
    public async Task Super_admin_only_permissions_cannot_be_granted_to_another_role(string role)
    {
        var attempted = new List<string> { Permissions.Portfolios.View };
        attempted.AddRange(Permissions.SuperAdminOnly);

        var result = await _service.SetPermissionsAsync(role, attempted, null);

        Assert.True(result.Succeeded);

        var granted = await _service.GetPermissionsAsync(role);
        Assert.Contains(Permissions.Portfolios.View, granted);

        foreach (var reserved in Permissions.SuperAdminOnly)
        {
            Assert.DoesNotContain(reserved, granted);
        }
    }

    [Fact]
    public async Task An_unknown_permission_is_ignored_rather_than_stored()
    {
        await _service.SetPermissionsAsync(
            Roles.Admin, [Permissions.Portfolios.View, "made.up.permission"], null);

        var granted = await _service.GetPermissionsAsync(Roles.Admin);

        Assert.Single(granted);
        Assert.DoesNotContain("made.up.permission", granted);
    }

    [Fact]
    public async Task Super_admin_permissions_cannot_be_edited()
    {
        var result = await _service.SetPermissionsAsync(
            Roles.SuperAdmin, [Permissions.Portfolios.View], null);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Permission_changes_are_audited_with_before_and_after()
    {
        await _service.SetPermissionsAsync(Roles.Admin, [Permissions.Portfolios.View], null);
        await _service.SetPermissionsAsync(Roles.Admin, [Permissions.Portfolios.Publish], null);

        var entry = _db.AuditLogs
            .Where(a => a.Action == AuditActions.PermissionsChanged)
            .OrderByDescending(a => a.Timestamp)
            .First();

        Assert.Contains(Permissions.Portfolios.View, entry.OldValue);
        Assert.Contains(Permissions.Portfolios.Publish, entry.NewValue);
    }

    [Fact]
    public async Task Creating_a_staff_account_is_audited()
    {
        await _service.CreateAsync("audited@msm.local", "A", "B", Roles.Admin, null);

        Assert.Contains(_db.AuditLogs,
            a => a.Action == AuditActions.StaffAccountCreated && a.NewValue!.Contains("audited@msm.local"));
    }
}
