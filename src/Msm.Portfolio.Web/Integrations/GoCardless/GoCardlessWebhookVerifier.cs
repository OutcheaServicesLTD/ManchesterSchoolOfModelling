using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Msm.Portfolio.Web.Configuration;

namespace Msm.Portfolio.Web.Integrations.GoCardless;

public interface IWebhookVerifier
{
    /// <summary>True when a signing secret is configured. Without one, nothing is trusted.</summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Whether the payload genuinely came from the provider (specification section 44).
    /// </summary>
    bool IsSignatureValid(string payload, string? signatureHeader);

    /// <summary>Reads the events out of a verified payload.</summary>
    IReadOnlyList<ProviderEvent> ParseEvents(string payload);
}

/// <summary>
/// Verifies and parses GoCardless webhooks.
/// </summary>
/// <remarks>
/// The webhook endpoint is unauthenticated by necessity — the provider has no session —
/// so the signature is the only thing separating a real payment notification from a
/// forged one. This is the security boundary of the whole payment flow, and it is pure
/// computation, so it is verified directly by tests without needing the provider.
/// </remarks>
public class GoCardlessWebhookVerifier(
    IOptions<IntegrationOptions> options,
    ILogger<GoCardlessWebhookVerifier> logger) : IWebhookVerifier
{
    private string? Secret => options.Value.GoCardless.WebhookSecret;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Secret);

    public bool IsSignatureValid(string payload, string? signatureHeader)
    {
        if (!IsConfigured)
        {
            // Refusing is the safe failure. Accepting unsigned webhooks would let anyone
            // who found the URL mark an order as paid and publish a portfolio.
            logger.LogError(
                "A webhook arrived but no signing secret is configured, so it cannot be trusted. "
                + "Set Integrations:GoCardless:WebhookSecret.");
            return false;
        }

        if (string.IsNullOrWhiteSpace(signatureHeader))
        {
            return false;
        }

        var expected = ComputeSignature(payload, Secret!);

        // Fixed-time comparison: a byte-by-byte early exit would leak, through timing,
        // how much of a guessed signature was correct.
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(expected),
            Encoding.ASCII.GetBytes(signatureHeader.Trim()));
    }

    /// <summary>HMAC-SHA256 of the raw request body, hex encoded.</summary>
    internal static string ComputeSignature(string payload, string secret)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));

        return Convert.ToHexStringLower(hash);
    }

    public IReadOnlyList<ProviderEvent> ParseEvents(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);

            if (!document.RootElement.TryGetProperty("events", out var events)
                || events.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var parsed = new List<ProviderEvent>(events.GetArrayLength());

            foreach (var element in events.EnumerateArray())
            {
                var id = Text(element, "id");

                // An event with no identifier cannot be de-duplicated, so it cannot be
                // processed safely and is dropped rather than risking double application.
                if (string.IsNullOrWhiteSpace(id))
                {
                    logger.LogWarning("Ignoring a webhook event with no id.");
                    continue;
                }

                element.TryGetProperty("links", out var links);

                parsed.Add(new ProviderEvent(
                    id!,
                    Text(element, "resource_type") ?? "unknown",
                    Text(element, "action") ?? "unknown",
                    Text(links, "payment"),
                    Text(links, "subscription"),
                    Text(links, "mandate"),
                    // GoCardless reports a failure cause under details.
                    element.TryGetProperty("details", out var details)
                        ? Text(details, "description") ?? Text(details, "cause")
                        : null));
            }

            return parsed;
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "A webhook payload could not be parsed as JSON.");
            return [];
        }
    }

    private static string? Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
