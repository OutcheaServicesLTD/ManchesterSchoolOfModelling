using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Msm.Portfolio.Web.Configuration;

namespace Msm.Portfolio.Web.Integrations.HighLevel;

/// <summary>
/// Updates a GoHighLevel contact's custom fields.
/// </summary>
/// <remarks>
/// <para>
/// <b>NOT YET VERIFIED AGAINST THE PROVIDER.</b> GoHighLevel's API and documentation
/// were both unreachable from the environment this was built in, so the endpoint, the
/// custom-field payload shape and the field keys below come from documented knowledge
/// rather than an observed exchange. Work through
/// <c>docs/gohighlevel-verification.md</c> before relying on it.
/// </para>
/// <para>
/// The stub stays the default until an API key is configured. A CRM push that silently
/// writes to the wrong fields would be worse than one that plainly does nothing.
/// </para>
/// </remarks>
public class HighLevelService : IHighLevelService
{
    private const string ApiVersion = "2021-07-28";

    private readonly HttpClient _http;
    private readonly ILogger<HighLevelService> _logger;
    private readonly HighLevelOptions _options;

    public HighLevelService(
        HttpClient http,
        IOptions<IntegrationOptions> options,
        ILogger<HighLevelService> logger)
    {
        _http = http;
        _logger = logger;
        _options = options.Value.HighLevel;

        _http.BaseAddress = new Uri(_options.BaseUrl.TrimEnd('/') + "/");
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        _http.DefaultRequestHeaders.Add("Version", ApiVersion);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _http.Timeout = TimeSpan.FromSeconds(20);
    }

    public bool IsLive => true;

    public async Task<CrmUpdateResult> UpdateContactAsync(
        string contactId, CrmContactFields fields, CancellationToken cancellationToken = default)
    {
        var payload = new
        {
            customFields = new object[]
            {
                Field(CrmFieldKeys.PortfolioUrl, fields.PortfolioUrl),
                Field(CrmFieldKeys.PortfolioStatus, fields.PortfolioStatus),
                Field(CrmFieldKeys.PurchaseStatus, fields.PurchaseStatus),
                Field(CrmFieldKeys.PurchaseDate, fields.PurchaseDate?.ToString("yyyy-MM-dd")),
                Field(CrmFieldKeys.MaintenanceStatus, fields.MaintenanceStatus),
                Field(CrmFieldKeys.PortfolioPublishedDate, fields.PortfolioPublishedDate?.ToString("yyyy-MM-dd"))
            }
        };

        try
        {
            using var content = new StringContent(
                JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            using var response = await _http.PutAsync($"contacts/{contactId}", content, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                return new CrmUpdateResult(true);
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            _logger.LogError(
                "GoHighLevel returned {Status} updating contact {ContactId}: {Body}",
                (int)response.StatusCode, contactId, body);

            // A contact that does not exist, or a request the CRM rejects as malformed,
            // will fail identically forever. Retrying it would occupy the queue and
            // never succeed, so it is marked terminal and left for a person.
            var retryable = response.StatusCode is not (
                HttpStatusCode.NotFound or HttpStatusCode.BadRequest
                or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden);

            return new CrmUpdateResult(false, $"CRM returned {(int)response.StatusCode}.", retryable);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // A CRM that is merely unreachable is exactly the case specification section
            // 45 cares about: retry, and never disturb the portfolio.
            _logger.LogWarning(ex, "Could not reach GoHighLevel to update contact {ContactId}.", contactId);

            return new CrmUpdateResult(false, "The CRM could not be reached.", IsRetryable: true);
        }
    }

    private static object Field(string key, string? value) => new { key, field_value = value ?? string.Empty };
}

/// <summary>
/// Custom field keys on the GoHighLevel contact (specification section 25). These must
/// match the fields configured in MSM's own GoHighLevel account.
/// </summary>
public static class CrmFieldKeys
{
    public const string PortfolioUrl = "portfolio_url";
    public const string PortfolioStatus = "portfolio_status";
    public const string PurchaseStatus = "purchase_status";
    public const string PurchaseDate = "purchase_date";
    public const string MaintenanceStatus = "maintenance_status";
    public const string PortfolioPublishedDate = "portfolio_published_date";
}
