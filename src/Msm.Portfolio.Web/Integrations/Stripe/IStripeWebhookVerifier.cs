using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Msm.Portfolio.Web.Configuration;

namespace Msm.Portfolio.Web.Integrations.Stripe;

/// <summary>One Stripe event, reduced to what the subscription flow needs from it.</summary>
/// <param name="EventId">
/// Stripe's own event identifier ("evt_..."). Stored and made unique against the same
/// PaymentWebhookEvents table GoCardless uses, which is what makes handling idempotent
/// under Stripe's own retries (specification section 44, extended to Stripe).
/// </param>
public record StripeWebhookEvent(
    string EventId,
    string Type,
    string? SubscriptionId,
    string? CustomerId,
    string? ClientReferenceId,
    string? ClientIdFromMetadata,
    string? Reason);

public interface IStripeWebhookVerifier
{
    /// <summary>True when a signing secret is configured. Without one, nothing is trusted.</summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Whether the payload genuinely came from Stripe (specification version 2, item 3).
    /// </summary>
    bool IsSignatureValid(string payload, string? signatureHeader);

    /// <summary>Reads the one event out of a verified payload.</summary>
    StripeWebhookEvent? ParseEvent(string payload);
}

/// <summary>
/// Verifies and parses Stripe webhooks.
/// </summary>
/// <remarks>
/// <para>
/// Implements Stripe's documented signing scheme directly — the same approach
/// <c>GoCardlessWebhookVerifier</c> takes for GoCardless — rather than trusting the SDK's
/// own event deserialisation to match this application's exact Stripe.net version. The
/// scheme itself is simple and stable: the <c>Stripe-Signature</c> header carries a
/// timestamp and an HMAC-SHA256 of <c>"{timestamp}.{payload}"</c>, keyed with the
/// webhook's signing secret.
/// </para>
/// <para>
/// The timestamp is checked against a tolerance window as well as the signature itself.
/// Without that, a signature captured once — from a compromised log, say — would stay
/// valid forever; Stripe's own libraries apply the same five-minute tolerance.
/// </para>
/// </remarks>
public class StripeWebhookVerifier(
    IOptions<IntegrationOptions> options,
    ILogger<StripeWebhookVerifier> logger) : IStripeWebhookVerifier
{
    private static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(5);

    private string? Secret => options.Value.Stripe.WebhookSecret;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Secret);

    public bool IsSignatureValid(string payload, string? signatureHeader)
    {
        if (!IsConfigured)
        {
            // Refusing is the safe failure. Accepting unsigned webhooks would let anyone
            // who found the URL activate or cancel a subscription for any client.
            logger.LogError(
                "A Stripe webhook arrived but no signing secret is configured, so it cannot be "
                + "trusted. Set Integrations:Stripe:WebhookSecret.");
            return false;
        }

        if (string.IsNullOrWhiteSpace(signatureHeader))
        {
            return false;
        }

        long? timestamp = null;
        string? signature = null;

        // "t=1614556800,v1=abc123...". Older signing secrets can produce more than one
        // v1 entry during a rotation; the first is enough — Stripe accepts any match.
        foreach (var part in signatureHeader.Split(',', StringSplitOptions.TrimEntries))
        {
            var pieces = part.Split('=', 2);
            if (pieces.Length != 2)
            {
                continue;
            }

            switch (pieces[0])
            {
                case "t" when long.TryParse(pieces[1], out var parsed):
                    timestamp = parsed;
                    break;
                case "v1" when signature is null:
                    signature = pieces[1];
                    break;
            }
        }

        if (timestamp is null || signature is null)
        {
            return false;
        }

        var age = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(timestamp.Value);

        if (age < TimeSpan.Zero || age > Tolerance)
        {
            logger.LogWarning("Rejected a Stripe webhook outside the signature tolerance window.");
            return false;
        }

        var expected = ComputeSignature(timestamp.Value, payload, Secret!);

        // Fixed-time comparison: a byte-by-byte early exit would leak, through timing,
        // how much of a guessed signature was correct.
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(signature));
    }

    /// <summary>HMAC-SHA256 of "{timestamp}.{payload}", hex encoded, per Stripe's scheme.</summary>
    internal static string ComputeSignature(long timestamp, string payload, string secret)
    {
        var signedPayload = $"{timestamp}.{payload}";

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(signedPayload));

        return Convert.ToHexStringLower(hash);
    }

    public StripeWebhookEvent? ParseEvent(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;

            var id = Text(root, "id");
            var type = Text(root, "type");

            // An event with no identifier cannot be de-duplicated, so it cannot be
            // processed safely.
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(type))
            {
                logger.LogWarning("Ignoring a Stripe webhook event with no id or type.");
                return null;
            }

            if (!root.TryGetProperty("data", out var data)
                || !data.TryGetProperty("object", out var obj))
            {
                return new StripeWebhookEvent(id, type, null, null, null, null, null);
            }

            obj.TryGetProperty("metadata", out var metadata);

            var subscriptionId = Text(obj, "subscription") ?? (type.StartsWith("customer.subscription.")
                ? Text(obj, "id")
                : null);

            return new StripeWebhookEvent(
                id,
                type,
                SubscriptionId: subscriptionId,
                CustomerId: Text(obj, "customer"),
                ClientReferenceId: Text(obj, "client_reference_id"),
                ClientIdFromMetadata: Text(metadata, "clientId"),
                Reason: Text(obj, "cancellation_reason")
                    ?? DottedText(obj, "last_finalization_error", "message"));
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "A Stripe webhook payload could not be parsed as JSON.");
            return null;
        }
    }

    private static string? Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? DottedText(JsonElement element, string property, string nested) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value)
            ? Text(value, nested)
            : null;
}
