using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Msm.Portfolio.Web.Configuration;
using Msm.Portfolio.Web.Data;
using Msm.Portfolio.Web.Domain.Entities;
using Msm.Portfolio.Web.Domain.Enums;
using Msm.Portfolio.Web.Integrations.GoCardless;
using Msm.Portfolio.Web.Services;

namespace Msm.Portfolio.Tests;

public class CheckoutAndWebhookTests : IDisposable
{
    private const string Secret = "signing-secret";

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _db;
    private readonly CheckoutService _checkout;
    private readonly PaymentWebhookProcessor _webhooks;
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
            new SilentBiographyWriter(), NullLogger<PortfolioService>.Instance);

        var commerce = new OptionsWrapper<CommerceOptions>(new CommerceOptions());

        _checkout = new CheckoutService(
            _db, _provider, portfolios, audit, notifications, commerce,
            NullLogger<CheckoutService>.Instance);

        var maintenance = new MaintenanceService(
            _db, portfolios, audit, notifications, commerce, NullLogger<MaintenanceService>.Instance);

        var verifier = new GoCardlessWebhookVerifier(
            new OptionsWrapper<IntegrationOptions>(new IntegrationOptions
            {
                GoCardless = new GoCardlessOptions { WebhookSecret = Secret }
            }),
            NullLogger<GoCardlessWebhookVerifier>.Instance);

        _webhooks = new PaymentWebhookProcessor(
            _db, verifier, _checkout, maintenance, audit, notifications,
            NullLogger<PaymentWebhookProcessor>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private void SeedProducts()
    {
        var programme = new Product
        {
            Code = ProductCodes.ModelDevelopmentProgramme,
            Name = "4 Week Model Development Programme",
            Price = 3499.00m,
            Currency = "GBP",
            BillingType = BillingType.OneOff
        };

        _db.Products.AddRange(programme, new Product
        {
            Code = ProductCodes.PortfolioMaintenance,
            Name = "Portfolio Maintenance",
            Price = 19.99m,
            Currency = "GBP",
            BillingType = BillingType.Recurring,
            BillingInterval = BillingInterval.Monthly
        });

        _db.SaveChanges();
        _programmeProductId = programme.Id;
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

    private string SignedPayload(params string[] eventJson) =>
        $$"""{"events":[{{string.Join(",", eventJson)}}]}""";

    private static string Sign(string payload) =>
        GoCardlessWebhookVerifier.ComputeSignature(payload, Secret);

    private static string PaymentEvent(string id, string action, string paymentId, string? reason = null) =>
        $$"""
        {"id":"{{id}}","resource_type":"payments","action":"{{action}}",
         "links":{"payment":"{{paymentId}}"}
         {{(reason is null ? "" : $",\"details\":{{\"description\":\"{reason}\"}}")}}}
        """;

    // ---------- Orders ----------

    [Fact]
    public async Task Opening_checkout_creates_an_order_at_the_programme_price()
    {
        var clientId = AddClient();

        var result = await _checkout.OpenAsync(clientId, null);

        Assert.True(result.Succeeded);
        Assert.Equal(3499.00m, result.Order!.Amount);
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

        _db.Products.Single(p => p.Id == _programmeProductId).Price = 3999.00m;
        await _db.SaveChangesAsync();

        Assert.Equal(3499.00m, (await _checkout.GetOrderAsync(order.Id))!.Amount);
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

    [Fact]
    public async Task A_successful_payment_starts_the_maintenance_subscription_at_todays_price()
    {
        var clientId = AddClient();
        var order = (await _checkout.OpenAsync(clientId, null)).Order!;
        await _checkout.BeginPaymentAsync(order.Id, "https://x/s", "https://x/f");
        await _checkout.CompleteAsync(order.Id);

        var subscription = _db.MaintenanceSubscriptions.Single(s => s.ClientId == clientId);
        Assert.Equal(19.99m, subscription.PriceAtCreation);
        Assert.Equal(MaintenanceSubscriptionStatus.NotStarted, subscription.Status);
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

    // ---------- Webhooks ----------

    [Fact]
    public async Task A_webhook_with_a_bad_signature_is_rejected_and_changes_nothing()
    {
        var payload = SignedPayload(PaymentEvent("EV1", "confirmed", "PM1"));

        var result = await _webhooks.ProcessAsync(payload, "wrong-signature");

        Assert.False(result.Accepted);
        Assert.Empty(_db.PaymentWebhookEvents);
    }

    /// <summary>
    /// Specification section 44: providers retry until they get a success, so the same
    /// event arrives repeatedly and must be applied only once.
    /// </summary>
    [Fact]
    public async Task Replaying_the_same_event_applies_it_only_once()
    {
        var clientId = AddClient();
        var order = (await _checkout.OpenAsync(clientId, null)).Order!;
        await _checkout.BeginPaymentAsync(order.Id, "https://x/s", "https://x/f");

        var transaction = _db.PaymentTransactions.Single(t => t.OrderId == order.Id);
        transaction.ProviderPaymentId = "PM1";
        await _db.SaveChangesAsync();

        var payload = SignedPayload(PaymentEvent("EV1", "confirmed", "PM1"));
        var signature = Sign(payload);

        var first = await _webhooks.ProcessAsync(payload, signature);
        var second = await _webhooks.ProcessAsync(payload, signature);
        var third = await _webhooks.ProcessAsync(payload, signature);

        Assert.Equal(1, first.Processed);
        Assert.Equal(0, second.Processed);
        Assert.Equal(1, second.Skipped);
        Assert.Equal(1, third.Skipped);

        // Accepted every time, so the provider stops retrying.
        Assert.True(second.Accepted);
        Assert.Equal(1, await _db.PaymentWebhookEvents.CountAsync());
        Assert.Equal(1, await _db.Orders.CountAsync(o => o.Status == OrderStatus.Confirmed));
    }

    /// <summary>
    /// The webhook is the authority and does not need the browser: a client who closed
    /// the tab still gets their portfolio published.
    /// </summary>
    [Fact]
    public async Task A_webhook_alone_confirms_the_order_and_publishes()
    {
        var clientId = AddClient();
        var order = (await _checkout.OpenAsync(clientId, null)).Order!;
        await _checkout.BeginPaymentAsync(order.Id, "https://x/s", "https://x/f");

        var transaction = _db.PaymentTransactions.Single(t => t.OrderId == order.Id);
        transaction.ProviderPaymentId = "PM1";
        await _db.SaveChangesAsync();

        var payload = SignedPayload(PaymentEvent("EV1", "confirmed", "PM1"));
        await _webhooks.ProcessAsync(payload, Sign(payload));

        Assert.Equal(OrderStatus.Confirmed, _db.Orders.Single().Status);
        Assert.True(_db.Portfolios.Single(p => p.ClientId == clientId).IsPublished);
    }

    [Fact]
    public async Task A_webhook_for_an_unknown_payment_is_recorded_but_changes_nothing()
    {
        var payload = SignedPayload(PaymentEvent("EV1", "confirmed", "PM-does-not-exist"));

        var result = await _webhooks.ProcessAsync(payload, Sign(payload));

        Assert.True(result.Accepted);
        Assert.Equal(1, await _db.PaymentWebhookEvents.CountAsync());
        Assert.Empty(_db.Orders);
    }

    [Fact]
    public async Task Several_events_in_one_payload_are_all_recorded()
    {
        var payload = SignedPayload(
            PaymentEvent("EV1", "created", "PM1"),
            PaymentEvent("EV2", "submitted", "PM1"),
            PaymentEvent("EV3", "confirmed", "PM1"));

        var result = await _webhooks.ProcessAsync(payload, Sign(payload));

        Assert.True(result.Accepted);
        Assert.Equal(3, await _db.PaymentWebhookEvents.CountAsync());
    }

    [Fact]
    public async Task A_failure_webhook_before_confirmation_fails_the_order()
    {
        var clientId = AddClient();
        var order = (await _checkout.OpenAsync(clientId, null)).Order!;
        await _checkout.BeginPaymentAsync(order.Id, "https://x/s", "https://x/f");

        var transaction = _db.PaymentTransactions.Single(t => t.OrderId == order.Id);
        transaction.ProviderPaymentId = "PM1";
        await _db.SaveChangesAsync();

        var payload = SignedPayload(PaymentEvent("EV1", "failed", "PM1", "Insufficient funds"));
        await _webhooks.ProcessAsync(payload, Sign(payload));

        Assert.Equal(OrderStatus.Failed, _db.Orders.Single().Status);
        Assert.False(_db.Portfolios.Single(p => p.ClientId == clientId).IsPublished);
    }

    /// <summary>
    /// A failure arriving after settlement concerns the money, not the sale. Tearing the
    /// portfolio down here would bypass the grace period in specification section 23.
    /// </summary>
    [Fact]
    public async Task A_failure_after_confirmation_notifies_staff_without_unpublishing()
    {
        var clientId = AddClient();
        var order = (await _checkout.OpenAsync(clientId, null)).Order!;
        await _checkout.BeginPaymentAsync(order.Id, "https://x/s", "https://x/f");
        await _checkout.CompleteAsync(order.Id);

        var transaction = _db.PaymentTransactions.Single(t => t.OrderId == order.Id);
        transaction.ProviderPaymentId = "PM1";
        await _db.SaveChangesAsync();

        var payload = SignedPayload(PaymentEvent("EV1", "charged_back", "PM1", "Chargeback"));
        await _webhooks.ProcessAsync(payload, Sign(payload));

        Assert.Equal(OrderStatus.Confirmed, _db.Orders.Single().Status);
        Assert.True(_db.Portfolios.Single(p => p.ClientId == clientId).IsPublished);
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
internal class FakeProvider : IGoCardlessService
{
    public bool IsLive => false;

    public CheckoutOutcome NextOutcome { get; set; } = new(true, "PM-DEFAULT", "MD-DEFAULT");

    public Task<CheckoutSession> CreateCheckoutAsync(
        Order order, ClientProfile client, string successUrl, string failureUrl,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new CheckoutSession($"BR-{order.Id:N}"[..12], successUrl));

    public Task<CheckoutOutcome> CompleteCheckoutAsync(
        string providerReference, CancellationToken cancellationToken = default) =>
        Task.FromResult(NextOutcome);
}
