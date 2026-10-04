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
using Msm.Portfolio.Web.Storage;

namespace Msm.Portfolio.Tests;

/// <summary>
/// Real signed payloads through the real verifier and processor against a real
/// database, so idempotency and the state changes it drives are exercised end to end
/// rather than through a mock of the boundary that matters most. Covers both things
/// that land on the one Stripe webhook: the £99 one-off portfolio purchase
/// (specification sections 19-21) and the portfolio-maintenance subscription
/// (specification version 2, item 3).
/// </summary>
public class StripeWebhookProcessorTests : IDisposable
{
    private const string Secret = "whsec_test";

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _db;
    private readonly StripeWebhookProcessor _processor;
    private readonly ICheckoutService _checkout;
    private readonly FakeProvider _purchaseProvider = new();
    private readonly Guid _maintenanceProductId;

    public StripeWebhookProcessorTests()
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
        _db.Products.Add(new Product
        {
            Code = ProductCodes.DigitalPortfolioYear,
            Name = "Digital Portfolio",
            Price = 99.00m,
            Currency = "GBP",
            BillingType = BillingType.OneOff
        });
        _db.SaveChanges();
        _maintenanceProductId = product.Id;

        var audit = new AuditService(_db);
        var notifications = new NotificationService(_db);

        var portfolios = new PortfolioService(
            _db, new SlugService(_db), new InMemoryStorage(), audit, notifications,
            NullLogger<PortfolioService>.Instance);

        var commerce = new OptionsWrapper<CommerceOptions>(
            new CommerceOptions { MaintenanceGracePeriodDays = 7 });

        var maintenance = new MaintenanceService(
            _db, portfolios, audit, notifications, commerce, NullLogger<MaintenanceService>.Instance);

        _checkout = new CheckoutService(
            _db, _purchaseProvider, portfolios, audit, notifications, commerce,
            NullLogger<CheckoutService>.Instance);

        var verifier = new StripeWebhookVerifier(
            new OptionsWrapper<IntegrationOptions>(new IntegrationOptions
            {
                Stripe = new StripeOptions { WebhookSecret = Secret }
            }),
            NullLogger<StripeWebhookVerifier>.Instance);

        _processor = new StripeWebhookProcessor(
            _db, verifier, _checkout, maintenance, NullLogger<StripeWebhookProcessor>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private Guid AddClient(string name = "Test Model")
    {
        var userId = Guid.CreateVersion7();
        var clientId = Guid.CreateVersion7();

        _db.Users.Add(new ApplicationUser { Id = userId, UserName = $"{clientId:N}@x.com", Email = $"{clientId:N}@x.com" });
        _db.ClientProfiles.Add(new ClientProfile
        {
            Id = clientId, ApplicationUserId = userId, FirstName = name, LastName = "Model",
            DateOfBirth = new DateOnly(1998, 1, 1)
        });
        _db.SaveChanges();

        return clientId;
    }

    private Guid AddSubscribedPublishedClient(
        Guid clientId, MaintenanceSubscriptionStatus status, string providerSubscriptionId)
    {
        var portfolio = new Msm.Portfolio.Web.Domain.Entities.Portfolio
        {
            ClientId = clientId, Slug = "test-model", IsPublished = true,
            PublishedAt = DateTimeOffset.UtcNow, Status = PortfolioStatus.Published
        };
        _db.Portfolios.Add(portfolio);

        _db.MaintenanceSubscriptions.Add(new MaintenanceSubscription
        {
            ClientId = clientId, ProductId = _maintenanceProductId, Provider = "Stripe",
            ProviderSubscriptionId = providerSubscriptionId, PriceAtCreation = 19.99m,
            Currency = "GBP", Status = status
        });

        _db.SaveChanges();

        return clientId;
    }

    private static string Sign(string payload)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return $"t={timestamp},v1={StripeWebhookVerifier.ComputeSignature(timestamp, payload, Secret)}";
    }

    /// <summary>
    /// Takes a client all the way to an open £99 checkout, the same way
    /// <c>CheckoutController</c> does, so the order carries a real
    /// <c>StripeCheckoutSessionId</c> a webhook can be matched against.
    /// </summary>
    private async Task<(Guid ClientId, string SessionId)> OpenPurchaseAsync()
    {
        var clientId = AddClient();

        _db.Portfolios.Add(new Msm.Portfolio.Web.Domain.Entities.Portfolio
        {
            ClientId = clientId, Status = PortfolioStatus.InViewing
        });
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
        await _db.SaveChangesAsync();

        var portfolio = _db.Portfolios.Single(p => p.ClientId == clientId);
        portfolio.FeaturedMediaId = asset.Id;
        await _db.SaveChangesAsync();

        var order = (await _checkout.OpenAsync(clientId, null)).Order!;
        await _checkout.BeginPaymentAsync(order.Id, "https://x/s", "https://x/f");

        return (clientId, (await _checkout.GetOrderAsync(order.Id))!.StripeCheckoutSessionId!);
    }

    [Fact]
    public async Task Checkout_completed_activates_a_subscription_for_the_referenced_client()
    {
        var clientId = AddClient();

        var payload = """
            {"id":"evt_1","type":"checkout.session.completed",
             "data":{"object":{"id":"cs_1","customer":"cus_1","subscription":"sub_1",
                                "client_reference_id":"CLIENT_ID"}}}
            """.Replace("CLIENT_ID", clientId.ToString());

        var result = await _processor.ProcessAsync(payload, Sign(payload));

        Assert.True(result.Accepted);
        Assert.Equal(1, result.Processed);

        var subscription = _db.MaintenanceSubscriptions.Single(s => s.ClientId == clientId);
        Assert.Equal(MaintenanceSubscriptionStatus.Active, subscription.Status);
        Assert.Equal("sub_1", subscription.ProviderSubscriptionId);
        Assert.Equal("cus_1", _db.ClientProfiles.Single(c => c.Id == clientId).StripeCustomerId);
    }

    /// <summary>
    /// Providers retry until they get a success, so the same event arrives more than
    /// once as a matter of course (specification section 44, extended to Stripe).
    /// </summary>
    [Fact]
    public async Task A_repeated_event_is_not_applied_twice()
    {
        var clientId = AddClient();

        var payload = """
            {"id":"evt_dup","type":"checkout.session.completed",
             "data":{"object":{"id":"cs_1","customer":"cus_1","subscription":"sub_1",
                                "client_reference_id":"CLIENT_ID"}}}
            """.Replace("CLIENT_ID", clientId.ToString());
        var signature = Sign(payload);

        await _processor.ProcessAsync(payload, signature);
        var second = await _processor.ProcessAsync(payload, signature);

        Assert.True(second.Accepted);
        Assert.Equal(0, second.Processed);
        Assert.Equal(1, second.Skipped);
        Assert.Equal(1, _db.MaintenanceSubscriptions.Count(s => s.ClientId == clientId));
    }

    [Fact]
    public async Task An_incorrectly_signed_payload_is_rejected()
    {
        const string payload = """{"id":"evt_1","type":"invoice.paid","data":{"object":{}}}""";

        var result = await _processor.ProcessAsync(payload, "t=1,v1=not-a-real-signature");

        Assert.False(result.Accepted);
    }

    [Fact]
    public async Task Invoice_paid_clears_a_payment_issue()
    {
        var clientId = AddClient();
        AddSubscribedPublishedClient(clientId, MaintenanceSubscriptionStatus.PaymentIssue, "sub_2");

        var payload = """
        {"id":"evt_2","type":"invoice.paid",
         "data":{"object":{"id":"in_1","customer":"cus_1","subscription":"sub_2"}}}
        """;

        await _processor.ProcessAsync(payload, Sign(payload));

        Assert.Equal(
            MaintenanceSubscriptionStatus.Active,
            _db.MaintenanceSubscriptions.Single(s => s.ClientId == clientId).Status);
    }

    [Fact]
    public async Task Invoice_payment_failed_opens_a_grace_period()
    {
        var clientId = AddClient();
        AddSubscribedPublishedClient(clientId, MaintenanceSubscriptionStatus.Active, "sub_3");

        var payload = """
        {"id":"evt_3","type":"invoice.payment_failed",
         "data":{"object":{"id":"in_2","customer":"cus_1","subscription":"sub_3",
                            "last_finalization_error":{"message":"Card declined."}}}}
        """;

        await _processor.ProcessAsync(payload, Sign(payload));

        var subscription = _db.MaintenanceSubscriptions.Single(s => s.ClientId == clientId);
        Assert.Equal(MaintenanceSubscriptionStatus.PaymentIssue, subscription.Status);
        Assert.NotNull(subscription.GracePeriodEndsAt);
        // Still public: the grace period is what keeps it up (specification section 23).
        Assert.True(_db.Portfolios.Single(p => p.ClientId == clientId).IsPublished);
    }

    [Fact]
    public async Task Subscription_deleted_ends_entitlement_and_unpublishes()
    {
        var clientId = AddClient();
        AddSubscribedPublishedClient(clientId, MaintenanceSubscriptionStatus.Active, "sub_4");

        var payload = """
        {"id":"evt_4","type":"customer.subscription.deleted",
         "data":{"object":{"id":"sub_4","customer":"cus_1"}}}
        """;

        await _processor.ProcessAsync(payload, Sign(payload));

        Assert.Equal(
            MaintenanceSubscriptionStatus.Cancelled,
            _db.MaintenanceSubscriptions.Single(s => s.ClientId == clientId).Status);
        Assert.False(_db.Portfolios.Single(p => p.ClientId == clientId).IsPublished);
    }

    /// <summary>
    /// Stripe does not guarantee delivery order. An invoice event for a subscription
    /// this application has not recorded yet must not throw — only checkout.session.
    /// completed has a client reference to create the row from.
    /// </summary>
    [Fact]
    public async Task An_event_for_an_unknown_subscription_is_accepted_and_changes_nothing()
    {
        const string payload = """
        {"id":"evt_5","type":"invoice.paid",
         "data":{"object":{"id":"in_3","customer":"cus_x","subscription":"sub_unknown"}}}
        """;

        var result = await _processor.ProcessAsync(payload, Sign(payload));

        Assert.True(result.Accepted);
        Assert.Empty(_db.MaintenanceSubscriptions);
    }

    // ---------- The £99 one-off portfolio purchase ----------

    /// <summary>
    /// A completed Checkout Session with no subscription attached is the one-off £99
    /// purchase, not the membership above — nothing else in this application opens a
    /// Stripe Checkout Session.
    /// </summary>
    [Fact]
    public async Task Checkout_completed_confirms_the_purchase_and_publishes_the_portfolio()
    {
        var (clientId, sessionId) = await OpenPurchaseAsync();

        var payload = """
            {"id":"evt_p1","type":"checkout.session.completed",
             "data":{"object":{"id":"SESSION_ID","payment_intent":"pi_1"}}}
            """.Replace("SESSION_ID", sessionId);

        var result = await _processor.ProcessAsync(payload, Sign(payload));

        Assert.True(result.Accepted);
        Assert.Equal(1, result.Processed);

        var order = _db.Orders.Single(o => o.ClientId == clientId);
        Assert.Equal(OrderStatus.Confirmed, order.Status);
        Assert.True(_db.Portfolios.Single(p => p.ClientId == clientId).IsPublished);
    }

    /// <summary>
    /// The webhook is the authority and does not need the browser: a client who closed
    /// the tab still gets their portfolio published (specification section 44).
    /// </summary>
    [Fact]
    public async Task Replaying_the_same_purchase_event_confirms_it_only_once()
    {
        var (_, sessionId) = await OpenPurchaseAsync();

        var payload = """
            {"id":"evt_p2","type":"checkout.session.completed",
             "data":{"object":{"id":"SESSION_ID","payment_intent":"pi_2"}}}
            """.Replace("SESSION_ID", sessionId);
        var signature = Sign(payload);

        var first = await _processor.ProcessAsync(payload, signature);
        var second = await _processor.ProcessAsync(payload, signature);

        Assert.Equal(1, first.Processed);
        Assert.Equal(0, second.Processed);
        Assert.Equal(1, second.Skipped);
        Assert.Equal(1, await _db.Orders.CountAsync(o => o.Status == OrderStatus.Confirmed));
    }

    [Fact]
    public async Task A_completed_session_matching_no_order_is_recorded_but_changes_nothing()
    {
        const string payload = """
            {"id":"evt_p3","type":"checkout.session.completed",
             "data":{"object":{"id":"cs_does_not_exist","payment_intent":"pi_3"}}}
            """;

        var result = await _processor.ProcessAsync(payload, Sign(payload));

        Assert.True(result.Accepted);
        Assert.Equal(1, await _db.PaymentWebhookEvents.CountAsync());
        Assert.Empty(_db.Orders);
    }

    /// <summary>
    /// Stripe's default Checkout Session expiry (24 hours unused) fails an order still
    /// waiting on it, so staff are not left looking at a sale that quietly never happened.
    /// </summary>
    [Fact]
    public async Task An_expired_session_fails_the_still_waiting_order()
    {
        var (clientId, sessionId) = await OpenPurchaseAsync();

        var payload = """
            {"id":"evt_p4","type":"checkout.session.expired",
             "data":{"object":{"id":"SESSION_ID"}}}
            """.Replace("SESSION_ID", sessionId);

        await _processor.ProcessAsync(payload, Sign(payload));

        Assert.Equal(OrderStatus.Failed, _db.Orders.Single(o => o.ClientId == clientId).Status);
        Assert.False(_db.Portfolios.Single(p => p.ClientId == clientId).IsPublished);
    }

    /// <summary>
    /// An expiry arriving for an order a later session already paid must not undo the
    /// sale — Stripe does not guarantee delivery order.
    /// </summary>
    [Fact]
    public async Task An_expired_session_for_an_already_confirmed_order_changes_nothing()
    {
        var (clientId, sessionId) = await OpenPurchaseAsync();

        var completed = """
            {"id":"evt_p5a","type":"checkout.session.completed",
             "data":{"object":{"id":"SESSION_ID","payment_intent":"pi_5"}}}
            """.Replace("SESSION_ID", sessionId);
        await _processor.ProcessAsync(completed, Sign(completed));

        var expired = """
            {"id":"evt_p5b","type":"checkout.session.expired",
             "data":{"object":{"id":"SESSION_ID"}}}
            """.Replace("SESSION_ID", sessionId);
        await _processor.ProcessAsync(expired, Sign(expired));

        Assert.Equal(OrderStatus.Confirmed, _db.Orders.Single(o => o.ClientId == clientId).Status);
        Assert.True(_db.Portfolios.Single(p => p.ClientId == clientId).IsPublished);
    }
}
