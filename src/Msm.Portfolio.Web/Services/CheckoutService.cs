using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Msm.Portfolio.Web.Configuration;
using Msm.Portfolio.Web.Data;
using Msm.Portfolio.Web.Domain.Entities;
using Msm.Portfolio.Web.Domain.Enums;
using Msm.Portfolio.Web.Integrations.GoCardless;

namespace Msm.Portfolio.Web.Services;

public record CheckoutStart(bool Succeeded, Order? Order = null, string? Error = null);

public interface ICheckoutService
{
    /// <summary>
    /// Opens a checkout for the programme (specification section 20). Returns the
    /// existing unfinished order rather than creating a second one.
    /// </summary>
    Task<CheckoutStart> OpenAsync(Guid clientId, Guid? userId, CancellationToken cancellationToken = default);

    Task<Order?> GetOrderAsync(Guid orderId, CancellationToken cancellationToken = default);

    /// <summary>Sends the client to the provider's hosted payment page.</summary>
    Task<(bool Succeeded, string? RedirectUrl, string? Error)> BeginPaymentAsync(
        Guid orderId, string successUrl, string failureUrl, CancellationToken cancellationToken = default);

    /// <summary>
    /// Confirms the outcome with the provider and, when authorised, activates the
    /// purchase (specification section 21).
    /// </summary>
    Task<OperationResult> CompleteAsync(Guid orderId, CancellationToken cancellationToken = default);

    Task<OperationResult> CancelAsync(Guid orderId, Guid? userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records the sale and publishes the portfolio.
    /// </summary>
    /// <remarks>
    /// Exposed because the webhook processor activates purchases too. A client who
    /// closed the tab mid-payment never returns to the success page, so activation
    /// cannot depend on the browser coming back (specification section 44).
    /// </remarks>
    Task ActivateAsync(
        Order order, string? providerPaymentId, PaymentStatus paymentStatus,
        CancellationToken cancellationToken = default);
}

public class CheckoutService(
    ApplicationDbContext db,
    IGoCardlessService provider,
    IPortfolioService portfolios,
    IAuditService audit,
    INotificationService notifications,
    IOptions<CommerceOptions> commerceOptions,
    ILogger<CheckoutService> logger) : ICheckoutService
{
    public async Task<CheckoutStart> OpenAsync(
        Guid clientId, Guid? userId, CancellationToken cancellationToken = default)
    {
        var client = await db.ClientProfiles
            .Include(c => c.Portfolio)
            .Include(c => c.GuardianConsent)
            .FirstOrDefaultAsync(c => c.Id == clientId, cancellationToken);

        if (client?.Portfolio is null)
        {
            return new CheckoutStart(false, Error: "That client could not be found.");
        }

        // A minor cannot reach purchase without guardian approval. Checked before any
        // money is requested rather than after (specification section 11).
        if (client.IsBlockedPendingGuardianConsent(DateOnly.FromDateTime(DateTime.UtcNow)))
        {
            return new CheckoutStart(false,
                Error: "This client is under 18 and their guardian has not yet approved.");
        }

        // Checked before the status rule below: a client who has paid is already past
        // the viewing stage, and "already purchased" is the useful answer rather than
        // a complaint that the portfolio is not in viewing.
        var existingConfirmed = await db.Orders.AnyAsync(
            o => o.ClientId == clientId && o.Status == OrderStatus.Confirmed, cancellationToken);

        if (existingConfirmed)
        {
            return new CheckoutStart(false, Error: "This client has already purchased the programme.");
        }

        // The journey in specification section 20 is: the client sees their prepared
        // portfolio, agrees, and only then does Admin open the checkout. Enforced here
        // as well as in the page, so opening the URL directly cannot take payment for a
        // portfolio nobody has been shown.
        if (client.Portfolio.Status is not (PortfolioStatus.InViewing or PortfolioStatus.AwaitingPurchase))
        {
            return new CheckoutStart(false,
                Error: "This portfolio has not been shown to the client yet.");
        }

        // Reuse an unfinished order rather than opening a second one, so a client who
        // abandoned the provider page and came back does not end up with two.
        var order = await db.Orders
            .Include(o => o.Product)
            .Where(o => o.ClientId == clientId
                        && (o.Status == OrderStatus.Draft || o.Status == OrderStatus.CheckoutStarted
                            || o.Status == OrderStatus.AwaitingPayment))
            .OrderByDescending(o => o.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (order is not null)
        {
            return new CheckoutStart(true, order);
        }

        var product = await db.Products.FirstOrDefaultAsync(
            p => p.Code == ProductCodes.ModelDevelopmentProgramme && p.IsActive, cancellationToken);

        if (product is null)
        {
            logger.LogError("The programme product is missing or inactive; checkout cannot open.");
            return new CheckoutStart(false, Error: "The programme is not available for purchase.");
        }

        order = new Order
        {
            ClientId = clientId,
            ProductId = product.Id,
            // Copied from the product now and never read back from it, so a later price
            // change cannot alter what this client agreed to (specification section 19).
            Amount = product.Price,
            Currency = product.Currency,
            Status = OrderStatus.Draft
        };

        db.Orders.Add(order);

        if (client.Portfolio.Status == PortfolioStatus.InViewing)
        {
            client.Portfolio.Status = PortfolioStatus.AwaitingPurchase;
            client.Portfolio.UpdatedAt = DateTimeOffset.UtcNow;
        }

        audit.Record(nameof(Order), order.Id.ToString(), AuditActions.CheckoutOpened,
            userId: userId, newValue: $"{order.Amount:0.00} {order.Currency} for client {clientId}");

        await db.SaveChangesAsync(cancellationToken);

        // Reloaded with the product so callers can show what is being bought.
        order.Product = product;

        return new CheckoutStart(true, order);
    }

    public Task<Order?> GetOrderAsync(Guid orderId, CancellationToken cancellationToken = default) =>
        db.Orders
            .Include(o => o.Product)
            .Include(o => o.Client)
            .Include(o => o.Transactions)
            .FirstOrDefaultAsync(o => o.Id == orderId, cancellationToken);

    public async Task<(bool Succeeded, string? RedirectUrl, string? Error)> BeginPaymentAsync(
        Guid orderId, string successUrl, string failureUrl, CancellationToken cancellationToken = default)
    {
        var order = await GetOrderAsync(orderId, cancellationToken);

        if (order is null)
        {
            return (false, null, "That order could not be found.");
        }

        if (order.Status == OrderStatus.Confirmed)
        {
            return (false, null, "This order has already been paid.");
        }

        try
        {
            var session = await provider.CreateCheckoutAsync(
                order, order.Client, successUrl, failureUrl, cancellationToken);

            order.GoCardlessReference = session.ProviderReference;
            order.Status = OrderStatus.CheckoutStarted;
            order.CheckoutStartedAt ??= DateTimeOffset.UtcNow;

            // A transaction row exists from the moment a payment is attempted, so an
            // abandoned or failed attempt is still visible to staff.
            db.PaymentTransactions.Add(new PaymentTransaction
            {
                OrderId = order.Id,
                Provider = "GoCardless",
                Amount = order.Amount,
                Currency = order.Currency,
                Status = PaymentStatus.CheckoutStarted
            });

            audit.Record(nameof(Order), order.Id.ToString(), AuditActions.CheckoutStarted,
                newValue: session.ProviderReference);

            await db.SaveChangesAsync(cancellationToken);

            return (true, session.RedirectUrl, null);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not open a payment for order {OrderId}.", orderId);
            return (false, null, "We could not open the payment page. Please try again.");
        }
    }

    public async Task<OperationResult> CompleteAsync(
        Guid orderId, CancellationToken cancellationToken = default)
    {
        var order = await GetOrderAsync(orderId, cancellationToken);

        if (order is null)
        {
            return OperationResult.Fail("That order could not be found.");
        }

        // Already done. Returning success means a client refreshing the confirmation
        // page sees confirmation rather than an error.
        if (order.Status == OrderStatus.Confirmed)
        {
            return OperationResult.Ok();
        }

        if (string.IsNullOrWhiteSpace(order.GoCardlessReference))
        {
            return OperationResult.Fail("This order has no payment attached.");
        }

        var outcome = await provider.CompleteCheckoutAsync(order.GoCardlessReference, cancellationToken);

        if (!outcome.Authorised)
        {
            order.Status = OrderStatus.Failed;
            RecordTransaction(order, PaymentStatus.Failed, null, outcome.FailureReason);

            audit.Record(nameof(Order), order.Id.ToString(), AuditActions.PaymentFailed,
                newValue: outcome.FailureReason);

            await db.SaveChangesAsync(cancellationToken);

            return OperationResult.Fail(outcome.FailureReason ?? "The payment was not completed.");
        }

        await ActivateAsync(order, outcome.ProviderPaymentId, PaymentStatus.Authorised, cancellationToken);

        return OperationResult.Ok();
    }

    public async Task<OperationResult> CancelAsync(
        Guid orderId, Guid? userId, CancellationToken cancellationToken = default)
    {
        var order = await GetOrderAsync(orderId, cancellationToken);

        if (order is null)
        {
            return OperationResult.Fail("That order could not be found.");
        }

        if (order.Status == OrderStatus.Confirmed)
        {
            // All sales are final and there is no self-service refund, so a paid order
            // is not cancellable here (specification section 24).
            return OperationResult.Fail("A completed purchase cannot be cancelled here.");
        }

        order.Status = OrderStatus.Cancelled;

        audit.Record(nameof(Order), order.Id.ToString(), AuditActions.CheckoutCancelled, userId: userId);
        await db.SaveChangesAsync(cancellationToken);

        return OperationResult.Ok();
    }

    /// <summary>
    /// Records the sale and publishes the portfolio (specification sections 20 and 21).
    /// </summary>
    /// <remarks>
    /// MSM want the portfolio live immediately on a successful checkout, so publication
    /// follows authorisation rather than waiting for settlement; later webhooks update
    /// the underlying payment state. Publication can still be refused — an under-18
    /// client without guardian approval, for instance — and when it is, the sale stands
    /// and staff are told, because the client has paid either way.
    /// </remarks>
    public async Task ActivateAsync(
        Order order,
        string? providerPaymentId,
        PaymentStatus paymentStatus,
        CancellationToken cancellationToken = default)
    {
        order.Status = OrderStatus.Confirmed;
        order.ConfirmedAt ??= DateTimeOffset.UtcNow;

        RecordTransaction(order, paymentStatus, providerPaymentId, null);

        audit.Record(nameof(Order), order.Id.ToString(), AuditActions.PaymentConfirmed,
            newValue: $"{order.Amount:0.00} {order.Currency}");

        var portfolio = await db.Portfolios
            .FirstOrDefaultAsync(p => p.ClientId == order.ClientId, cancellationToken);

        if (portfolio is not null && portfolio.Status != PortfolioStatus.Published)
        {
            portfolio.Status = PortfolioStatus.Purchased;
            portfolio.UpdatedAt = DateTimeOffset.UtcNow;
        }

        // Purchase status and date are CRM-visible, so the contact needs updating. Only
        // marked here; the push happens on a worker (specification section 45).
        portfolio?.RequestCrmSync();

        await StartMaintenanceAsync(order, cancellationToken);

        // Saved before publishing so the sale is durable even if publication is refused.
        await db.SaveChangesAsync(cancellationToken);

        var published = await portfolios.PublishAsync(order.ClientId, null, cancellationToken);

        if (!published.Succeeded)
        {
            logger.LogWarning(
                "Order {OrderId} was paid but the portfolio could not be published: {Reason}",
                order.Id, published.Error);

            await notifications.NotifyStaffAsync(
                NotificationTypes.PublicationBlockedAfterPurchase,
                $"{order.Client.PublicName} has paid but their portfolio could not be published: {published.Error}",
                $"/admin/clients/{order.ClientId}",
                cancellationToken);

            await db.SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Creates the maintenance subscription record at the configured offset
    /// (specification section 22). Collection itself is Phase 8; this fixes the price
    /// agreed today so a later change cannot alter it.
    /// </summary>
    private async Task StartMaintenanceAsync(Order order, CancellationToken cancellationToken)
    {
        if (await db.MaintenanceSubscriptions.AnyAsync(s => s.ClientId == order.ClientId, cancellationToken))
        {
            return;
        }

        var product = await db.Products.FirstOrDefaultAsync(
            p => p.Code == ProductCodes.PortfolioMaintenance && p.IsActive, cancellationToken);

        if (product is null)
        {
            return;
        }

        var commerce = commerceOptions.Value;

        db.MaintenanceSubscriptions.Add(new MaintenanceSubscription
        {
            ClientId = order.ClientId,
            ProductId = product.Id,
            PriceAtCreation = product.Price,
            Currency = product.Currency,
            Status = MaintenanceSubscriptionStatus.NotStarted,
            StartDate = DateTimeOffset.UtcNow.AddDays(commerce.MaintenanceStartsAfterDays)
        });
    }

    private void RecordTransaction(
        Order order, PaymentStatus status, string? providerPaymentId, string? failureReason)
    {
        // Update the attempt already in flight rather than adding a row per state change,
        // so one payment reads as one transaction.
        var transaction = order.Transactions
            .OrderByDescending(t => t.CreatedAt)
            .FirstOrDefault(t => t.Status is PaymentStatus.Pending or PaymentStatus.CheckoutStarted);

        if (transaction is null)
        {
            transaction = new PaymentTransaction
            {
                OrderId = order.Id,
                Provider = "GoCardless",
                Amount = order.Amount,
                Currency = order.Currency
            };

            db.PaymentTransactions.Add(transaction);
        }

        transaction.Status = status;
        transaction.ProviderPaymentId = providerPaymentId ?? transaction.ProviderPaymentId;
        transaction.FailureReason = failureReason;
        transaction.UpdatedAt = DateTimeOffset.UtcNow;
    }
}
