using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Msm.Portfolio.Web.Configuration;
using Msm.Portfolio.Web.Data;
using Msm.Portfolio.Web.Domain.Entities;
using Msm.Portfolio.Web.Domain.Enums;
using Msm.Portfolio.Web.Services;

namespace Msm.Portfolio.Tests;

public class MaintenanceServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _db;
    private readonly MaintenanceService _service;
    private readonly PublicPortfolioService _public;
    private Guid _maintenanceProductId;

    private static readonly CommerceOptions Commerce = new() { MaintenanceGracePeriodDays = 7 };

    public MaintenanceServiceTests()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();
        _db = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();

        var product = new Product
        {
            Code = ProductCodes.PortfolioMaintenance,
            Name = "Portfolio Maintenance",
            Price = 19.99m,
            Currency = "GBP",
            BillingType = BillingType.Recurring,
            BillingInterval = BillingInterval.Monthly
        };
        _db.Products.Add(product);
        _db.SaveChanges();
        _maintenanceProductId = product.Id;

        var audit = new AuditService(_db);
        var notifications = new NotificationService(_db);

        var portfolios = new PortfolioService(
            _db, new SlugService(_db), new InMemoryStorage(), audit, notifications,
            NullLogger<PortfolioService>.Instance);

        _service = new MaintenanceService(
            _db, portfolios, audit, notifications,
            new OptionsWrapper<CommerceOptions>(Commerce), NullLogger<MaintenanceService>.Instance);

        _public = new PublicPortfolioService(
            _db,
            new MeasurementTemplateProvider(
                new StaticOptionsMonitor<MeasurementTemplateOptions>(new MeasurementTemplateOptions())),
            notifications,
            NullLogger<PublicPortfolioService>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private Guid AddPublishedClient(
        MaintenanceSubscriptionStatus status = MaintenanceSubscriptionStatus.Active,
        DateTimeOffset? graceEnds = null,
        string slug = "emma-johnson")
    {
        var userId = Guid.CreateVersion7();
        var clientId = Guid.CreateVersion7();

        _db.Users.Add(new ApplicationUser { Id = userId, UserName = $"{clientId:N}@x.com", Email = $"{clientId:N}@x.com" });
        _db.ClientProfiles.Add(new ClientProfile
        {
            Id = clientId, ApplicationUserId = userId,
            FirstName = "Emma", LastName = "Johnson", DateOfBirth = new DateOnly(1998, 1, 1)
        });

        var portfolio = new Msm.Portfolio.Web.Domain.Entities.Portfolio
        {
            ClientId = clientId,
            Slug = slug,
            IsPublished = true,
            IsVisibleOnModelBoard = true,
            PublishedAt = DateTimeOffset.UtcNow,
            Status = PortfolioStatus.Published
        };
        _db.Portfolios.Add(portfolio);
        _db.SaveChanges();

        var asset = new MediaAsset
        {
            ClientId = clientId,
            StorageKey = $"clients/{clientId:N}/a/original.jpg",
            OriginalFilename = "a.jpg", MimeType = "image/jpeg", FileSize = 10,
            MediaType = MediaType.Image, IsSelectedForPortfolio = true, IsFeatured = true
        };
        _db.MediaAssets.Add(asset);
        _db.SaveChanges();
        portfolio.FeaturedMediaId = asset.Id;

        _db.MaintenanceSubscriptions.Add(new MaintenanceSubscription
        {
            ClientId = clientId,
            ProductId = _maintenanceProductId,
            ProviderSubscriptionId = $"SB-{clientId:N}"[..12],
            PriceAtCreation = 19.99m,
            Currency = "GBP",
            Status = status,
            GracePeriodEndsAt = graceEnds
        });

        _db.SaveChanges();

        return clientId;
    }

    private MaintenanceSubscription SubscriptionFor(Guid clientId) =>
        _db.MaintenanceSubscriptions.Single(s => s.ClientId == clientId);

    private Msm.Portfolio.Web.Domain.Entities.Portfolio PortfolioFor(Guid clientId) =>
        _db.Portfolios.Single(p => p.ClientId == clientId);

    // ---------- Failure opens a grace period ----------

    /// <summary>
    /// Specification section 23 is explicit: the portfolio stays public through the
    /// grace period. Only the warning appears.
    /// </summary>
    [Fact]
    public async Task A_failed_payment_opens_a_grace_period_and_leaves_the_portfolio_live()
    {
        var clientId = AddPublishedClient();

        Assert.True((await _service.RecordPaymentFailureAsync(clientId, "Insufficient funds")).Succeeded);

        var subscription = SubscriptionFor(clientId);
        Assert.Equal(MaintenanceSubscriptionStatus.PaymentIssue, subscription.Status);
        Assert.NotNull(subscription.GracePeriodEndsAt);

        var portfolio = PortfolioFor(clientId);
        Assert.True(portfolio.IsPublished);
        Assert.Equal(PortfolioStatus.PaymentWarning, portfolio.Status);
    }

    [Fact]
    public async Task The_grace_period_is_the_configured_length()
    {
        var clientId = AddPublishedClient();
        var before = DateTimeOffset.UtcNow;

        await _service.RecordPaymentFailureAsync(clientId, null);

        var endsAt = SubscriptionFor(clientId).GracePeriodEndsAt!.Value;
        var days = (endsAt - before).TotalDays;

        Assert.InRange(days, 6.9, 7.1);
    }

    /// <summary>
    /// A repeatedly failing payment must not keep extending the deadline, or a
    /// portfolio would stay live indefinitely without being paid for.
    /// </summary>
    [Fact]
    public async Task A_second_failure_does_not_restart_the_clock()
    {
        var clientId = AddPublishedClient();

        await _service.RecordPaymentFailureAsync(clientId, "First failure");
        var firstDeadline = SubscriptionFor(clientId).GracePeriodEndsAt;

        await Task.Delay(20);
        await _service.RecordPaymentFailureAsync(clientId, "Second failure");

        Assert.Equal(firstDeadline, SubscriptionFor(clientId).GracePeriodEndsAt);
    }

    [Fact]
    public async Task A_failed_payment_notifies_both_staff_and_the_client()
    {
        var clientId = AddPublishedClient();
        var client = _db.ClientProfiles.Single(c => c.Id == clientId);

        var adminRole = new ApplicationRole(Msm.Portfolio.Web.Authorization.Roles.Admin) { Id = Guid.CreateVersion7() };
        var admin = new ApplicationUser { Id = Guid.CreateVersion7(), UserName = "a@msm.local", Email = "a@msm.local" };
        _db.Roles.Add(adminRole);
        _db.Users.Add(admin);
        _db.UserRoles.Add(new Microsoft.AspNetCore.Identity.IdentityUserRole<Guid>
        {
            RoleId = adminRole.Id, UserId = admin.Id
        });
        await _db.SaveChangesAsync();

        await _service.RecordPaymentFailureAsync(clientId, "Declined");

        Assert.Contains(_db.Notifications,
            n => n.UserId == admin.Id && n.Type == NotificationTypes.MaintenancePaymentFailed);
        Assert.Contains(_db.Notifications,
            n => n.UserId == client.ApplicationUserId && n.Type == NotificationTypes.MaintenancePaymentFailed);
    }

    // ---------- Resolution ----------

    [Fact]
    public async Task Resolving_within_the_grace_period_clears_the_warning()
    {
        var clientId = AddPublishedClient();
        await _service.RecordPaymentFailureAsync(clientId, null);

        Assert.True((await _service.RecordPaymentSuccessAsync(clientId)).Succeeded);

        var subscription = SubscriptionFor(clientId);
        Assert.Equal(MaintenanceSubscriptionStatus.Active, subscription.Status);
        Assert.Null(subscription.GracePeriodEndsAt);

        var portfolio = PortfolioFor(clientId);
        Assert.True(portfolio.IsPublished);
        Assert.Equal(PortfolioStatus.Published, portfolio.Status);
        Assert.Null(await _service.GetWarningAsync(clientId));
    }

    // ---------- Expiry ----------

    [Fact]
    public async Task An_elapsed_grace_period_unpublishes_the_portfolio()
    {
        var clientId = AddPublishedClient(
            MaintenanceSubscriptionStatus.PaymentIssue,
            graceEnds: DateTimeOffset.UtcNow.AddMinutes(-1));

        var unpublished = await _service.ExpireElapsedGracePeriodsAsync();

        Assert.Equal(1, unpublished);
        Assert.Equal(MaintenanceSubscriptionStatus.GracePeriodExpired, SubscriptionFor(clientId).Status);

        var portfolio = PortfolioFor(clientId);
        Assert.False(portfolio.IsPublished);
        Assert.Equal(PortfolioStatus.Unpublished, portfolio.Status);
    }

    [Fact]
    public async Task A_grace_period_still_running_is_left_alone()
    {
        var clientId = AddPublishedClient(
            MaintenanceSubscriptionStatus.PaymentIssue,
            graceEnds: DateTimeOffset.UtcNow.AddDays(3));

        Assert.Equal(0, await _service.ExpireElapsedGracePeriodsAsync());
        Assert.True(PortfolioFor(clientId).IsPublished);
    }

    /// <summary>
    /// The worker runs repeatedly, so expiring must be safe to re-run: expiring one
    /// moves it out of the set the query matches.
    /// </summary>
    [Fact]
    public async Task Expiring_twice_takes_a_portfolio_down_only_once()
    {
        AddPublishedClient(
            MaintenanceSubscriptionStatus.PaymentIssue,
            graceEnds: DateTimeOffset.UtcNow.AddMinutes(-1));

        Assert.Equal(1, await _service.ExpireElapsedGracePeriodsAsync());
        Assert.Equal(0, await _service.ExpireElapsedGracePeriodsAsync());
    }

    [Fact]
    public async Task Expiry_removes_the_model_from_the_board_and_takes_the_page_down()
    {
        // Seeded with the grace period still running, so the model starts on the board.
        var clientId = AddPublishedClient(
            MaintenanceSubscriptionStatus.PaymentIssue,
            graceEnds: DateTimeOffset.UtcNow.AddDays(2));

        Assert.Single(await _public.GetModelBoardAsync());
        Assert.NotNull(await _public.GetBySlugAsync("emma-johnson"));

        // Time passes and the deadline is reached.
        SubscriptionFor(clientId).GracePeriodEndsAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await _db.SaveChangesAsync();

        await _service.ExpireElapsedGracePeriodsAsync();

        Assert.Empty(await _public.GetModelBoardAsync());
        Assert.Null(await _public.GetBySlugAsync("emma-johnson"));
    }

    /// <summary>
    /// Republishing after payment is resolved is a deliberate act, not automatic, so a
    /// portfolio does not silently reappear.
    /// </summary>
    [Fact]
    public async Task Paying_after_expiry_does_not_republish_automatically()
    {
        var clientId = AddPublishedClient(
            MaintenanceSubscriptionStatus.PaymentIssue,
            graceEnds: DateTimeOffset.UtcNow.AddMinutes(-1));
        await _service.ExpireElapsedGracePeriodsAsync();

        await _service.RecordPaymentSuccessAsync(clientId);

        Assert.Equal(MaintenanceSubscriptionStatus.Active, SubscriptionFor(clientId).Status);
        Assert.False(PortfolioFor(clientId).IsPublished);
    }

    // ---------- Warnings ----------

    [Fact]
    public async Task The_warning_counts_down_the_remaining_days()
    {
        var clientId = AddPublishedClient(
            MaintenanceSubscriptionStatus.PaymentIssue,
            graceEnds: DateTimeOffset.UtcNow.AddDays(3).AddMinutes(1));

        var warning = await _service.GetWarningAsync(clientId);

        Assert.NotNull(warning);
        Assert.Equal(4, warning!.DaysRemaining);
        Assert.False(warning.PortfolioTakenDown);
    }

    [Fact]
    public async Task The_warning_says_so_once_the_portfolio_has_been_taken_down()
    {
        var clientId = AddPublishedClient(
            MaintenanceSubscriptionStatus.PaymentIssue,
            graceEnds: DateTimeOffset.UtcNow.AddMinutes(-1));
        await _service.ExpireElapsedGracePeriodsAsync();

        var warning = await _service.GetWarningAsync(clientId);

        Assert.NotNull(warning);
        Assert.True(warning!.PortfolioTakenDown);
    }

    [Fact]
    public async Task A_healthy_subscription_produces_no_warning()
    {
        var clientId = AddPublishedClient();

        Assert.Null(await _service.GetWarningAsync(clientId));
    }

    // ---------- Entitlement ----------

    /// <summary>
    /// Specification section 18 requires an active entitlement for the Model Board. A
    /// failed payment inside its grace period keeps it, because section 23 says the
    /// portfolio stays live for those days.
    /// </summary>
    [Theory]
    [InlineData(MaintenanceSubscriptionStatus.NotStarted, true)]
    [InlineData(MaintenanceSubscriptionStatus.Active, true)]
    [InlineData(MaintenanceSubscriptionStatus.GracePeriodExpired, false)]
    [InlineData(MaintenanceSubscriptionStatus.Cancelled, false)]
    [InlineData(MaintenanceSubscriptionStatus.Ended, false)]
    public void Entitlement_follows_the_subscription_state(
        MaintenanceSubscriptionStatus status, bool expected)
    {
        var subscription = new MaintenanceSubscription { Status = status };

        Assert.Equal(expected, subscription.IsEntitlementActive(DateTimeOffset.UtcNow));
    }

    [Fact]
    public void A_payment_issue_keeps_entitlement_only_inside_the_grace_period()
    {
        var now = DateTimeOffset.UtcNow;

        var inGrace = new MaintenanceSubscription
        {
            Status = MaintenanceSubscriptionStatus.PaymentIssue,
            GracePeriodEndsAt = now.AddDays(2)
        };

        var elapsed = new MaintenanceSubscription
        {
            Status = MaintenanceSubscriptionStatus.PaymentIssue,
            GracePeriodEndsAt = now.AddDays(-1)
        };

        Assert.True(inGrace.IsEntitlementActive(now));
        Assert.False(elapsed.IsEntitlementActive(now));
        Assert.True(elapsed.HasGracePeriodExpired(now));
    }

    /// <summary>
    /// Covers the window between a grace period elapsing and the worker noticing: the
    /// model must not still be listed while unentitled.
    /// </summary>
    [Fact]
    public async Task An_unentitled_model_leaves_the_board_before_the_worker_runs()
    {
        AddPublishedClient(
            MaintenanceSubscriptionStatus.PaymentIssue,
            graceEnds: DateTimeOffset.UtcNow.AddMinutes(-1));

        // Nothing has expired anything yet; the portfolio is still published.
        Assert.Empty(await _public.GetModelBoardAsync());
    }

    [Fact]
    public async Task A_model_inside_the_grace_period_stays_on_the_board()
    {
        AddPublishedClient(
            MaintenanceSubscriptionStatus.PaymentIssue,
            graceEnds: DateTimeOffset.UtcNow.AddDays(2));

        Assert.Single(await _public.GetModelBoardAsync());
    }

    /// <summary>
    /// The payment problem is between MSM and the client. An agency viewing the public
    /// portfolio must see no sign of it (specification section 23).
    /// </summary>
    [Fact]
    public async Task A_payment_warning_is_never_visible_on_the_public_portfolio()
    {
        var clientId = AddPublishedClient();
        await _service.RecordPaymentFailureAsync(clientId, "Insufficient funds");

        var portfolio = await _public.GetBySlugAsync("emma-johnson");
        var serialised = System.Text.Json.JsonSerializer.Serialize(portfolio);

        Assert.NotNull(portfolio);
        Assert.DoesNotContain("Insufficient", serialised);
        Assert.DoesNotContain("PaymentWarning", serialised);
        Assert.DoesNotContain("grace", serialised, StringComparison.OrdinalIgnoreCase);
    }
}
