using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Msm.Portfolio.Web.Configuration;
using Msm.Portfolio.Web.Domain.Enums;
using Msm.Portfolio.Web.Integrations.GoCardless;
using Msm.Portfolio.Web.Services;

namespace Msm.Portfolio.Tests;

/// <summary>
/// The webhook endpoint is unauthenticated by necessity, so the signature is the only
/// thing separating a real payment notification from a forged one. It is pure
/// computation, so it is verified directly without needing the provider.
/// </summary>
public class WebhookVerifierTests
{
    private const string Secret = "a-shared-signing-secret";

    private static GoCardlessWebhookVerifier Verifier(string? secret = Secret) =>
        new(new OptionsWrapper<IntegrationOptions>(new IntegrationOptions
        {
            GoCardless = new GoCardlessOptions { WebhookSecret = secret }
        }), NullLogger<GoCardlessWebhookVerifier>.Instance);

    private static string Sign(string payload, string secret = Secret) =>
        GoCardlessWebhookVerifier.ComputeSignature(payload, secret);

    [Fact]
    public void A_correctly_signed_payload_is_accepted()
    {
        const string payload = """{"events":[{"id":"EV1"}]}""";

        Assert.True(Verifier().IsSignatureValid(payload, Sign(payload)));
    }

    [Fact]
    public void A_payload_signed_with_the_wrong_secret_is_rejected()
    {
        const string payload = """{"events":[{"id":"EV1"}]}""";

        Assert.False(Verifier().IsSignatureValid(payload, Sign(payload, "not-the-secret")));
    }

    /// <summary>
    /// The whole point of signing: a payload altered in transit no longer verifies.
    /// </summary>
    [Fact]
    public void A_tampered_payload_is_rejected()
    {
        const string original = """{"events":[{"id":"EV1","action":"failed"}]}""";
        const string tampered = """{"events":[{"id":"EV1","action":"confirmed"}]}""";

        Assert.False(Verifier().IsSignatureValid(tampered, Sign(original)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-signature")]
    public void A_missing_or_malformed_signature_is_rejected(string? signature)
    {
        Assert.False(Verifier().IsSignatureValid("""{"events":[]}""", signature));
    }

    /// <summary>
    /// Accepting unsigned webhooks would let anyone who found the URL mark an order as
    /// paid and publish a portfolio, so an unconfigured secret refuses everything.
    /// </summary>
    [Fact]
    public void Nothing_is_accepted_when_no_secret_is_configured()
    {
        const string payload = """{"events":[{"id":"EV1"}]}""";
        var verifier = Verifier(secret: null);

        Assert.False(verifier.IsConfigured);
        Assert.False(verifier.IsSignatureValid(payload, Sign(payload)));
        Assert.False(verifier.IsSignatureValid(payload, "anything"));
    }

    [Fact]
    public void Signatures_are_compared_case_and_whitespace_tolerantly_on_the_header()
    {
        const string payload = """{"events":[{"id":"EV1"}]}""";

        // Providers may pad the header value; the signature itself is lowercase hex.
        Assert.True(Verifier().IsSignatureValid(payload, $"  {Sign(payload)}  "));
    }

    [Fact]
    public void Events_are_parsed_with_their_links()
    {
        const string payload = """
        {"events":[
          {"id":"EV1","resource_type":"payments","action":"confirmed",
           "links":{"payment":"PM123"},"details":{"description":"All good"}},
          {"id":"EV2","resource_type":"mandates","action":"active",
           "links":{"mandate":"MD456"}}
        ]}
        """;

        var events = Verifier().ParseEvents(payload);

        Assert.Equal(2, events.Count);
        Assert.Equal("EV1", events[0].EventId);
        Assert.Equal("payments", events[0].ResourceType);
        Assert.Equal("confirmed", events[0].Action);
        Assert.Equal("PM123", events[0].PaymentId);
        Assert.Equal("All good", events[0].Reason);
        Assert.Equal("MD456", events[1].MandateId);
    }

    /// <summary>
    /// An event with no id cannot be de-duplicated, so it cannot be applied safely.
    /// </summary>
    [Fact]
    public void An_event_without_an_id_is_dropped()
    {
        const string payload = """
        {"events":[{"resource_type":"payments","action":"confirmed"},{"id":"EV2"}]}
        """;

        var events = Verifier().ParseEvents(payload);

        Assert.Single(events);
        Assert.Equal("EV2", events[0].EventId);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{}")]
    [InlineData("""{"events":"not-an-array"}""")]
    public void A_malformed_payload_yields_no_events_rather_than_throwing(string payload)
    {
        Assert.Empty(Verifier().ParseEvents(payload));
    }
}

/// <summary>The provider action to payment state mapping from specification section 21.</summary>
public class PaymentStatusMappingTests
{
    [Theory]
    [InlineData("created", PaymentStatus.Pending)]
    [InlineData("submitted", PaymentStatus.Submitted)]
    [InlineData("confirmed", PaymentStatus.Confirmed)]
    [InlineData("paid_out", PaymentStatus.Confirmed)]
    [InlineData("failed", PaymentStatus.Failed)]
    [InlineData("charged_back", PaymentStatus.Failed)]
    [InlineData("cancelled", PaymentStatus.Cancelled)]
    [InlineData("resubmission_requested", PaymentStatus.Reviewed)]
    public void Provider_actions_map_to_payment_states(string action, PaymentStatus expected)
    {
        Assert.Equal(expected, PaymentWebhookProcessor.MapPaymentStatus(action));
    }

    [Fact]
    public void Mapping_is_case_insensitive()
    {
        Assert.Equal(PaymentStatus.Confirmed, PaymentWebhookProcessor.MapPaymentStatus("CONFIRMED"));
    }

    /// <summary>
    /// A provider adding a new event type must not be able to corrupt an order, so an
    /// unrecognised action maps to nothing and changes no state.
    /// </summary>
    [Theory]
    [InlineData("some_future_action")]
    [InlineData("")]
    [InlineData("surcharge_fee_debited")]
    public void An_unknown_action_maps_to_nothing(string action)
    {
        Assert.Null(PaymentWebhookProcessor.MapPaymentStatus(action));
    }
}
