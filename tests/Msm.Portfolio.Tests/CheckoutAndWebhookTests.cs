using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Msm.Portfolio.Web.Configuration;
using Msm.Portfolio.Web.Data;
using Msm.Portfolio.Web.Domain.Entities;
using Msm.Portfolio.Web.Domain.Enums;
using Msm.Portfolio.Web.Integrations.Stripe;
using Msm.Portfolio.Web.Services;

namespace Msm.Portfolio.Tests;

/// <summary>
/// The £99 one-off portfolio purchase (specification sections 19 and 20), run through
/// <see cref="CheckoutService"/> against a fake Stripe checkout provider. Webhook
/// confirmation of the same purchase is covered in
/// <c>StripeWebhookProcessorTests</c>, alongside the portfolio-maintenance subscription
/// that shares the one Stripe webhook endpoint.
/// </summary>
public class CheckoutAndWebhookTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _db;
    private readonly CheckoutService _checkout;
    private readonly FakeProvider _provider = new();
    private Guid _programmeProductId;

    public CheckoutAndWebhookTests()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();
        _db = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();

        SeedProducts();

        var audit = new AuditService(_db);
        var notifications = new NotificationService(_db);

        var portfolios = new PortfolioService(
            _db, new SlugService(_db), new InMemoryStorage(), audit, notifications,
            NullLogger<PortfolioService>.Instance);

        var commerce = new OptionsWrapper<CommerceOptions>(new CommerceOptions());

        _checkout = new CheckoutService(
            _db, _provider, portfolios, audit, notifications, commerce,
            NullLogger<CheckoutService>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private void SeedProducts()
    {
        var portfolio = new Product
        {
            Code = ProductCodes.DigitalPortfolioYear,
            Name = "Digital Portfolio",
            Price = 99.00m,
            Currency = "GBP",
            BillingType = BillingType.OneOff
        };

        _db.Products.AddRange(portfolio, new Product
        {
            Code = ProductCodes.PortfolioMaintenance,
            Name = "Portfolio Maintenance",
            Price = 19.99m,
            Currency = "GBP",
            BillingType = BillingType.Recurring,
            BillingInterval = BillingInterval.Monthly
        });

        _db.SaveChanges();
        _programmeProductId = portfolio.Id;
    }

    private Guid AddClient(
        int age = 25,
        GuardianConsentStatus? guardian = null,
        PortfolioStatus status = PortfolioStatus.InViewing,
        bool withImage = true)
    {
        var userId = Guid.CreateVersion7();
        var clientId = Guid.CreateVersion7();

        _db.Users.Add(new ApplicationUser { Id = userId, UserName = $"{clientId:N}@x.com", Email = $"{clientId:N}@x.com" });
        _db.ClientProfiles.Add(new ClientProfile
        {
            Id = clientId,
            ApplicationUserId = userId,
            FirstName = "Emma",
            LastName = "Johnson",
            DateOfBirth = DateOnly.FromDateTime(DateTime.UtcNow).AddYears(-age)
        });

        var portfolio = new Msm.Portfolio.Web.Domain.Entities.Portfolio { ClientId = clientId, Status = status };
        _db.Portfolios.Add(portfolio);

        if (guardian is { } consent)
        {
            _db.GuardianConsents.Add(new GuardianConsent
            {
                ClientId = clientId, GuardianName = "G", Relationship = "Parent",
                Email = "g@example.com", VerificationToken = Guid.NewGuid().ToString("N"), Status = consent
            });
        }

        _db.SaveChanges();

        if (withImage)
        {
            var asset = new MediaAsset
            {
                ClientId = clientId,
                StorageKey = $"clients/{clientId:N}/a/original.jpg",
                OriginalFilename = "a.jpg",
                MimeType = "image/jpeg",
                FileSize = 10,
                MediaType = MediaType.Image,
                IsSelectedForPortfolio = true,
                IsFeatured = true
            };
            _db.MediaAssets.Add(asset);
            _db.SaveChanges();

            portfolio.FeaturedMediaId = asset.Id;
            _db.SaveChanges();
        }

        return clientId;
    }

    // ---------- Orders ----------

    [Fact]
    public async Task Opening_checkout_creates_an_order_at_the_portfolio_price()
    {
        var clientId = AddClient();

        var result = await _checkout.OpenAsync(clientId, null);

        Assert.True(result.Succeeded);
        Assert.Equal(99.00m, result.Order!.Amount);
        Assert.Equal("GBP", result.Order.Currency);
        Assert.Equal(OrderStatus.Draft, result.Order.Status);
        Assert.Equal(PortfolioStatus.AwaitingPurchase,
            _db.Portfolios.Single(p => p.ClientId == clientId).Status);
    }

    /// <summary>
    /// Specification section 19: the agreed amount is preserved on the order even if
    /// MSM later changes the advertised price.
    /// </summary>
    [Fact]
    public async Task A_later_price_change_does_not_alter_an_existing_order()
    {
        var clientId = AddClient();
        var order = (await _checkout.OpenAsync(clientId, null)).Order!;

        _db.Products.Single(p => p.Id == _programmeProductId).Price = 149.00m;
        await _db.SaveChangesAsync();

        Assert.Equal(99.00m, (await _checkout.GetOrderAsync(order.Id))!.Amount);
    }

    /// <summary>A client returning after abandoning the provider page must not be charged twice.</summary>
    [Fact]
    public async Task Reopening_checkout_reuses_the_unfinished_order()
    {
        var clientId = AddClient();

        var first = await _checkout.OpenAsync(clientId, null);
        var second = await _checkout.OpenAsync(clientId, null);

        Assert.Equal(first.Order!.Id, second.Order!.Id);
        Assert.Equal(1, await _db.Orders.CountAsync());
    }

    [Fact]
    public async Task A_client_who_has_already_paid_cannot_open_another_checkout()
    {
        var clientId = AddClient();
        var order = (await _checkout.OpenAsync(clientId, null)).Order!;
        await _checkout.BeginPaymentAsync(order.Id, "https://x/s", "https://x/f");
        await _checkout.CompleteAsync(order.Id);

        var second = await _checkout.OpenAsync(clientId, null);

        Assert.False(second.Succeeded);
        Assert.Contains("already purchased", second.Error);
    }

    /// <summary>
    /// Specification section 11: a minor cannot reach purchase. Checked before any money
    /// is requested rather than after.
    /// </summary>
    [Fact]
    public async Task A_minor_without_guardian_approval_cannot_open_checkout()
    {
        var clientId = AddClient(age: 16, guardian: GuardianConsentStatus.Pending);

        var result = await _checkout.OpenAsync(clientId, null);

        Assert.False(result.Succeeded);
        Assert.Contains("under 18", result.Error);
        Assert.Empty(_db.Orders);
    }

    [Fact]
    public async Task A_minor_with_guardian_approval_can_open_checkout()
    {
        var clientId = AddClient(age: 16, guardian: GuardianConsentStatus.Approved);

        Assert.True((await _checkout.OpenAsync(clientId, null)).Succeeded);
    }

    /// <summary>
    /// The journey in specification section 20 has the client see their portfolio before
    /// any money is requested. Enforced in the service as well as the page, so opening
    /// the URL directly cannot take payment for a portfolio nobody has been shown.
    /// </summary>
    [Theory]
    [InlineData(PortfolioStatus.AwaitingClientInformation)]
    [InlineData(PortfolioStatus.ReadyForRetoucher)]
    [InlineData(PortfolioStatus.Retouching)]
    [InlineData(PortfolioStatus.ReadyForReview)]
    [InlineData(PortfolioStatus.Archived)]
    public async Task Checkout_cannot_open_before_the_client_has_seen_their_portfolio(PortfolioStatus status)
    {
        var clientId = AddClient(status: status);

        var result = await _checkout.OpenAsync(clientId, null);

        Assert.False(result.Succeeded);
        Assert.Contains("not been shown", result.Error);
        Assert.Empty(_db.Orders);
    }

    [Theory]
    [InlineData(PortfolioStatus.InViewing)]
    [InlineData(PortfolioStatus.AwaitingPurchase)]
    public async Task Checkout_opens_once_the_portfolio_has_been_shown(PortfolioStatus status)
    {
        var clientId = AddClient(status: status);

        Assert.True((await _checkout.OpenAsync(clientId, null)).Succeeded);
    }

    // ---------- Payment and activation ----------

    [Fact]
    public async Task A_successful_payment_confirms_the_order_and_publishes_the_portfolio()
    {
        var clientId = AddClient();
        var order = (await _checkout.OpenAsync(clientId, null)).Order!;

        await _checkout.BeginPaymentAsync(order.Id, "https://x/s", "https://x/f");
        var result = await _checkout.CompleteAsync(order.Id);

        Assert.True(result.Succeeded);

        var stored = await _checkout.GetOrderAsync(order.Id);
        Assert.Equal(OrderStatus.Confirmed, stored!.Status);
        Assert.NotNull(stored.ConfirmedAt);

        var portfolio = _db.Portfolios.Single(p => p.ClientId == clientId);
        Assert.True(portfolio.IsPublished);
        Assert.Equal("emma-johnson", portfolio.Slug);
    }

    /// <summary>
    /// The £99 is the only payment, so nothing recurring is opened against the client. A
    /// subscription created here would sit waiting to fail a collection that is never
    /// attempted, and take the portfolio down when it did.
    /// </summary>
    [Fact]
    public async Task A_successful_payment_starts_no_subscription()
    {
        var clientId = AddClient();
        var order = (await _checkout.OpenAsync(clientId, null)).Order!;
        await _checkout.BeginPaymentAsync(order.Id, "https://x/s", "https://x/f");
        await _checkout.CompleteAsync(order.Id);

        Assert.Empty(_db.MaintenanceSubscriptions);
    }

    [Fact]
    public async Task A_purchase_costs_ninety_nine_pounds()
    {
        var clientId = AddClient();

        var order = (await _checkout.OpenAsync(clientId, null)).Order!;

        Assert.Equal(99.00m, order.Amount);
        Assert.Equal("GBP", order.Currency);
    }

    [Fact]
    public async Task A_purchase_keeps_the_portfolio_public_for_a_year()
    {
        var clientId = AddClient();
        var order = (await _checkout.OpenAsync(clientId, null)).Order!;
        await _checkout.BeginPaymentAsync(order.Id, "https://x/s", "https://x/f");
        await _checkout.CompleteAsync(order.Id);

        var portfolio = _db.Portfolios.Single(p => p.ClientId == clientId);

        Assert.NotNull(portfolio.ExpiresAt);

        // A day either side, so the test is not a clock comparison.
        var days = (portfolio.ExpiresAt!.Value - DateTimeOffset.UtcNow).TotalDays;
        Assert.InRange(days, 364, 366);
    }

    /// <summary>
    /// Somebody who renews early has paid for a year and should get a year. Replacing the
    /// expiry rather than adding to it would quietly take back the time they had left.
    /// </summary>
    [Fact]
    public async Task Buying_again_adds_a_year_to_what_is_left()
    {
        var clientId = AddClient();
        var portfolio = _db.Portfolios.Single(p => p.ClientId == clientId);
        portfolio.ExpiresAt = DateTimeOffset.UtcNow.AddDays(60);
        _db.SaveChanges();

        var order = (await _checkout.OpenAsync(clientId, null)).Order!;
        await _checkout.BeginPaymentAsync(order.Id, "https://x/s", "https://x/f");
        await _checkout.CompleteAsync(order.Id);

        _db.Entry(portfolio).Reload();

        var days = (portfolio.ExpiresAt!.Value - DateTimeOffset.UtcNow).TotalDays;
        Assert.InRange(days, 424, 426);
    }

    [Fact]
    public async Task A_failed_payment_leaves_the_portfolio_unpublished()
    {
        var clientId = AddClient();
        var order = (await _checkout.OpenAsync(clientId, null)).Order!;
        await _checkout.BeginPaymentAsync(order.Id, "https://x/s", "https://x/f");

        _provider.NextOutcome = new CheckoutOutcome(false, FailureReason: "Bank declined.");
        var result = await _checkout.CompleteAsync(order.Id);

        Assert.False(result.Succeeded);
        Assert.Equal(OrderStatus.Failed, (await _checkout.GetOrderAsync(order.Id))!.Status);
        Assert.False(_db.Portfolios.Single(p => p.ClientId == clientId).IsPublished);
    }

    /// <summary>
    /// Completing twice must not confirm twice; a client refreshing the confirmation
    /// page is not a second sale.
    /// </summary>
    [Fact]
    public async Task Completing_an_already_confirmed_order_is_a_no_op()
    {
        var clientId = AddClient();
        var order = (await _checkout.OpenAsync(clientId, null)).Order!;
        await _checkout.BeginPaymentAsync(order.Id, "https://x/s", "https://x/f");

        await _checkout.CompleteAsync(order.Id);
        var confirmedAt = (await _checkout.GetOrderAsync(order.Id))!.ConfirmedAt;

        Assert.True((await _checkout.CompleteAsync(order.Id)).Succeeded);
        Assert.Equal(confirmedAt, (await _checkout.GetOrderAsync(order.Id))!.ConfirmedAt);
    }

    /// <summary>
    /// Specification section 24: all sales are final, so a paid order is not cancellable.
    /// </summary>
    [Fact]
    public async Task A_confirmed_order_cannot_be_cancelled()
    {
        var clientId = AddClient();
        var order = (await _checkout.OpenAsync(clientId, null)).Order!;
        await _checkout.BeginPaymentAsync(order.Id, "https://x/s", "https://x/f");
        await _checkout.CompleteAsync(order.Id);

        var result = await _checkout.CancelAsync(order.Id, null);

        Assert.False(result.Succeeded);
        Assert.Equal(OrderStatus.Confirmed, (await _checkout.GetOrderAsync(order.Id))!.Status);
    }

    /// <summary>
    /// If payment succeeds but publication is refused, the sale still stands: the client
    /// has paid either way, and staff are told so a person can resolve it.
    /// </summary>
    [Fact]
    public async Task A_paid_order_stands_even_when_publication_is_refused()
    {
        // No image, so the portfolio cannot be published.
        var clientId = AddClient(withImage: false);
        var order = (await _checkout.OpenAsync(clientId, null)).Order!;
        await _checkout.BeginPaymentAsync(order.Id, "https://x/s", "https://x/f");

        await _checkout.CompleteAsync(order.Id);

        var stored = await _checkout.GetOrderAsync(order.Id);
        Assert.Equal(OrderStatus.Confirmed, stored!.Status);

        var portfolio = _db.Portfolios.Single(p => p.ClientId == clientId);
        Assert.False(portfolio.IsPublished);
        Assert.Equal(PortfolioStatus.Purchased, portfolio.Status);
    }
}

/// <summary>A provider that records what it was asked and returns a scripted outcome.</summary>
internal class FakeProvider : IStripeCheckoutService
{
    public bool IsLive => false;

    public CheckoutOutcome NextOutcome { get; set; } = new(true, "PI-DEFAULT");

    public Task<CheckoutSession> CreateCheckoutAsync(
        Order order, ClientProfile client, string successUrl, string failureUrl,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new CheckoutSession($"cs_{order.Id:N}"[..12], successUrl));

    public Task<CheckoutOutcome> CompleteCheckoutAsync(
        string providerReference, CancellationToken cancellationToken = default) =>
        Task.FromResult(NextOutcome);
}
