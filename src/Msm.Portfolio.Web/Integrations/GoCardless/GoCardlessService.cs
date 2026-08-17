using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Msm.Portfolio.Web.Configuration;
using Msm.Portfolio.Web.Domain.Entities;

namespace Msm.Portfolio.Web.Integrations.GoCardless;

/// <summary>
/// Calls the GoCardless Billing Requests API.
/// </summary>
/// <remarks>
/// <para>
/// <b>NOT YET VERIFIED AGAINST THE PROVIDER.</b> This was written without access to the
/// GoCardless sandbox or their documentation, so the request and response shapes below
/// are from documented knowledge rather than an observed exchange. Before taking real
/// payments, run the checkout against the sandbox and confirm each item in
/// <c>docs/gocardless-verification.md</c>. Until then the stub remains the default and
/// this type is only registered when an access token is configured.
/// </para>
/// <para>
/// The flow is: create a billing request describing what is being collected, create a
/// billing request flow to obtain a hosted page, send the client there, then read the
/// result back when they return. The hosted page is used deliberately rather than a
/// custom bank-details form, which would carry extra scheme-compliance obligations.
/// </para>
/// </remarks>
public class GoCardlessService : IGoCardlessService
{
    private const string ApiVersion = "2015-07-06";

    private readonly HttpClient _http;
    private readonly ILogger<GoCardlessService> _logger;
    private readonly GoCardlessOptions _options;

    public GoCardlessService(
        HttpClient http,
        IOptions<IntegrationOptions> options,
        ILogger<GoCardlessService> logger)
    {
        _http = http;
        _logger = logger;
        _options = options.Value.GoCardless;

        _http.BaseAddress = new Uri(_options.Environment.Equals("live", StringComparison.OrdinalIgnoreCase)
            ? "https://api.gocardless.com/"
            : "https://api-sandbox.gocardless.com/");

        _http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _options.AccessToken);
        _http.DefaultRequestHeaders.Add("GoCardless-Version", ApiVersion);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public bool IsLive => true;

    public async Task<CheckoutSession> CreateCheckoutAsync(
        Order order,
        ClientProfile client,
        string successUrl,
        string failureUrl,
        CancellationToken cancellationToken = default)
    {
        // Amount in the smallest currency unit: pence for GBP. Sending pounds here would
        // undercharge by a factor of a hundred, so it is converted explicitly.
        var pence = (int)decimal.Round(order.Amount * 100m, 0, MidpointRounding.AwayFromZero);

        var billingRequest = await PostAsync("billing_requests", new
        {
            billing_requests = new
            {
                payment_request = new
                {
                    description = "4 Week Model Development Programme",
                    amount = pence,
                    currency = order.Currency
                },
                metadata = new
                {
                    order_id = order.Id.ToString(),
                    client_id = order.ClientId.ToString()
                }
            }
        }, cancellationToken);

        var billingRequestId = Read(billingRequest, "billing_requests", "id")
            ?? throw new InvalidOperationException("GoCardless did not return a billing request id.");

        var flow = await PostAsync("billing_request_flows", new
        {
            billing_request_flows = new
            {
                redirect_uri = successUrl,
                exit_uri = failureUrl,
                links = new { billing_request = billingRequestId }
            }
        }, cancellationToken);

        var url = Read(flow, "billing_request_flows", "authorisation_url")
            ?? throw new InvalidOperationException("GoCardless did not return an authorisation URL.");

        _logger.LogInformation(
            "Opened GoCardless billing request {BillingRequestId} for order {OrderId}.",
            billingRequestId, order.Id);

        return new CheckoutSession(billingRequestId, url);
    }

    public async Task<CheckoutOutcome> CompleteCheckoutAsync(
        string providerReference, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _http.GetAsync(
                $"billing_requests/{providerReference}", cancellationToken);

            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError(
                    "GoCardless returned {Status} reading billing request {Reference}: {Body}",
                    (int)response.StatusCode, providerReference, body);

                return new CheckoutOutcome(false, FailureReason: "The payment could not be confirmed.");
            }

            using var document = JsonDocument.Parse(body);
            var request = document.RootElement.GetProperty("billing_requests");

            var status = request.TryGetProperty("status", out var s) ? s.GetString() : null;

            // Only a fulfilled request means the client actually completed the journey.
            // Returning to the success URL alone proves nothing: a client can reach that
            // address by going back, or by typing it.
            if (!string.Equals(status, "fulfilled", StringComparison.OrdinalIgnoreCase))
            {
                return new CheckoutOutcome(
                    false, FailureReason: $"The payment was not completed (status: {status ?? "unknown"}).");
            }

            var links = request.TryGetProperty("links", out var l) ? l : default;

            return new CheckoutOutcome(
                true,
                ProviderPaymentId: Value(links, "payment"),
                ProviderMandateId: Value(links, "mandate"));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            // A failure to reach the provider must not be read as a failed payment: the
            // client may well have paid. The webhook is the authority and will correct
            // the record when it arrives (specification section 44).
            _logger.LogError(ex, "Could not reach GoCardless to confirm {Reference}.", providerReference);

            return new CheckoutOutcome(
                false, FailureReason: "We could not confirm the payment. Please contact us before trying again.");
        }
    }

    private async Task<JsonDocument> PostAsync(string path, object payload, CancellationToken cancellationToken)
    {
        using var content = new StringContent(
            JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        // Idempotency key: a retried create must not open a second billing request, and
        // therefore must not be able to charge the client twice.
        content.Headers.Add("Idempotency-Key", Guid.CreateVersion7().ToString());

        using var response = await _http.PostAsync(path, content, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError(
                "GoCardless returned {Status} for {Path}: {Body}", (int)response.StatusCode, path, body);

            throw new InvalidOperationException($"GoCardless rejected the request to {path}.");
        }

        return JsonDocument.Parse(body);
    }

    private static string? Read(JsonDocument document, string envelope, string property) =>
        document.RootElement.TryGetProperty(envelope, out var inner) ? Value(inner, property) : null;

    private static string? Value(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
