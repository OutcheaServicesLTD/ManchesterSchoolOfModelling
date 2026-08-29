using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Msm.Portfolio.Web.Configuration;
using Msm.Portfolio.Web.Integrations.Stripe;

namespace Msm.Portfolio.Tests;

/// <summary>
/// As with GoCardless, the webhook endpoint is unauthenticated by necessity, so the
/// signature is the only thing separating a real Stripe notification from a forged one.
/// Pure computation, so verified directly without needing a Stripe account.
/// </summary>
public class StripeWebhookVerifierTests
{
    private const string Secret = "whsec_a-shared-signing-secret";

    private static StripeWebhookVerifier Verifier(string? secret = Secret) =>
        new(new OptionsWrapper<IntegrationOptions>(new IntegrationOptions
        {
            Stripe = new StripeOptions { WebhookSecret = secret }
        }), NullLogger<StripeWebhookVerifier>.Instance);

    private static string Header(string payload, long timestamp, string secret = Secret) =>
        $"t={timestamp},v1={StripeWebhookVerifier.ComputeSignature(timestamp, payload, secret)}";

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    [Fact]
    public void A_correctly_signed_payload_is_accepted()
    {
        const string payload = """{"id":"evt_1","type":"invoice.paid"}""";

        Assert.True(Verifier().IsSignatureValid(payload, Header(payload, Now())));
    }

    [Fact]
    public void A_payload_signed_with_the_wrong_secret_is_rejected()
    {
        const string payload = """{"id":"evt_1","type":"invoice.paid"}""";

        Assert.False(Verifier().IsSignatureValid(payload, Header(payload, Now(), "not-the-secret")));
    }

    /// <summary>The whole point of signing: a payload altered in transit no longer verifies.</summary>
    [Fact]
    public void A_tampered_payload_is_rejected()
    {
        const string original = """{"id":"evt_1","type":"invoice.paid"}""";
        const string tampered = """{"id":"evt_1","type":"invoice.payment_failed"}""";

        Assert.False(Verifier().IsSignatureValid(tampered, Header(original, Now())));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-signature")]
    [InlineData("t=not-a-number,v1=abc")]
    public void A_missing_or_malformed_signature_is_rejected(string? signature)
    {
        Assert.False(Verifier().IsSignatureValid("""{"id":"evt_1"}""", signature));
    }

    /// <summary>
    /// A signature captured once — from a compromised log, say — must not stay valid
    /// forever, so anything outside the tolerance window is refused.
    /// </summary>
    [Theory]
    [InlineData(-3600)]
    [InlineData(3600)]
    public void A_signature_outside_the_tolerance_window_is_rejected(int secondsOffset)
    {
        const string payload = """{"id":"evt_1"}""";
        var stale = Now() + secondsOffset;

        Assert.False(Verifier().IsSignatureValid(payload, Header(payload, stale)));
    }

    /// <summary>
    /// Accepting unsigned webhooks would let anyone who found the URL activate or
    /// cancel a subscription for any client, so an unconfigured secret refuses
    /// everything.
    /// </summary>
    [Fact]
    public void Nothing_is_accepted_when_no_secret_is_configured()
    {
        const string payload = """{"id":"evt_1"}""";
        var verifier = Verifier(secret: null);

        Assert.False(verifier.IsConfigured);
        Assert.False(verifier.IsSignatureValid(payload, Header(payload, Now())));
    }

    [Fact]
    public void A_checkout_completed_event_carries_the_client_reference_and_subscription()
    {
        const string payload = """
        {"id":"evt_1","type":"checkout.session.completed",
         "data":{"object":{"id":"cs_1","customer":"cus_1","subscription":"sub_1",
                            "client_reference_id":"11111111-1111-1111-1111-111111111111"}}}
        """;

        var evt = Verifier().ParseEvent(payload);

        Assert.NotNull(evt);
        Assert.Equal("checkout.session.completed", evt!.Type);
        Assert.Equal("sub_1", evt.SubscriptionId);
        Assert.Equal("cus_1", evt.CustomerId);
        Assert.Equal("11111111-1111-1111-1111-111111111111", evt.ClientReferenceId);
    }

    /// <summary>
    /// A subscription's own object has no top-level "subscription" field — it is one —
    /// so its own id is what identifies which client this event is about.
    /// </summary>
    [Fact]
    public void A_subscription_deleted_event_uses_the_objects_own_id()
    {
        const string payload = """
        {"id":"evt_2","type":"customer.subscription.deleted",
         "data":{"object":{"id":"sub_1","customer":"cus_1"}}}
        """;

        var evt = Verifier().ParseEvent(payload);

        Assert.Equal("sub_1", evt!.SubscriptionId);
    }

    [Fact]
    public void An_invoice_payment_failed_event_carries_the_failure_reason()
    {
        const string payload = """
        {"id":"evt_3","type":"invoice.payment_failed",
         "data":{"object":{"id":"in_1","customer":"cus_1","subscription":"sub_1",
                            "last_finalization_error":{"message":"Your card was declined."}}}}
        """;

        var evt = Verifier().ParseEvent(payload);

        Assert.Equal("sub_1", evt!.SubscriptionId);
        Assert.Equal("Your card was declined.", evt.Reason);
    }

    /// <summary>An event with no id or type cannot be de-duplicated or dispatched safely.</summary>
    [Theory]
    [InlineData("""{"type":"invoice.paid"}""")]
    [InlineData("""{"id":"evt_1"}""")]
    [InlineData("not json at all")]
    public void An_event_missing_essentials_yields_null(string payload)
    {
        Assert.Null(Verifier().ParseEvent(payload));
    }
}
