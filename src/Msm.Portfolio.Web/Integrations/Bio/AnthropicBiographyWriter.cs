using System.Text;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;
using Microsoft.Extensions.Options;

namespace Msm.Portfolio.Web.Integrations.Bio;

/// <summary>
/// Writes a suggested biography with Claude.
/// </summary>
/// <remarks>
/// <para>
/// What comes back is a draft for an administrator to read, edit and accept. That is the
/// whole safety argument for this feature: the text describes a real person, often a
/// young one, and is what an agency reads about them. A model given a height and a
/// location will happily produce fluent sentences about ambition, experience and
/// character that nobody has any basis for, so the instructions below forbid inventing
/// anything and a human still signs it off.
/// </para>
/// <para>
/// The facts are also all it is given. No photographs are sent.
/// </para>
/// </remarks>
public class AnthropicBiographyWriter : IBiographyWriter
{
    private readonly BiographyOptions _options;
    private readonly ILogger<AnthropicBiographyWriter> _logger;
    private readonly AnthropicClient _client;

    public AnthropicBiographyWriter(
        IOptions<BiographyOptions> options,
        ILogger<AnthropicBiographyWriter> logger)
    {
        _options = options.Value;
        _logger = logger;
        _client = new AnthropicClient { ApiKey = _options.ApiKey };
    }

    public bool IsEnabled => !string.IsNullOrWhiteSpace(_options.ApiKey);

    public async Task<BiographyDraftResult> WriteAsync(
        BiographyFacts facts, CancellationToken cancellationToken = default)
    {
        if (!IsEnabled)
        {
            return new BiographyDraftResult(false, null, "No biography provider is configured.");
        }

        try
        {
            var response = await _client.Messages.Create(new MessageCreateParams
            {
                Model = _options.Model,
                MaxTokens = 1024,
                System = SystemPrompt(_options.TargetWords),
                Messages = [new() { Role = Role.User, Content = Describe(facts) }],
            }, cancellationToken: cancellationToken);

            var text = string.Concat(
                    response.Content.Select(b => b.Value).OfType<TextBlock>().Select(b => b.Text))
                .Trim();

            if (string.IsNullOrWhiteSpace(text))
            {
                return new BiographyDraftResult(false, null, "The reply was empty.");
            }

            return new BiographyDraftResult(true, text, null);
        }
        // Most specific first, so a key that is wrong is not retried like a rate limit.
        catch (AnthropicUnauthorizedException ex)
        {
            _logger.LogError(ex, "The biography provider rejected the API key.");
            return new BiographyDraftResult(false, null, "The API key was rejected.");
        }
        catch (AnthropicNotFoundException ex)
        {
            _logger.LogError(ex, "The biography model {Model} was not found.", _options.Model);
            return new BiographyDraftResult(false, null, $"The model '{_options.Model}' was not found.");
        }
        catch (AnthropicRateLimitException ex)
        {
            _logger.LogWarning(ex, "The biography provider is rate limiting; this will be retried.");
            return new BiographyDraftResult(false, null, "Too many requests. This will be tried again.");
        }
        catch (Anthropic5xxException ex)
        {
            _logger.LogWarning(ex, "The biography provider failed; this will be retried.");
            return new BiographyDraftResult(false, null, "The provider is unavailable. This will be tried again.");
        }
        catch (AnthropicApiException ex)
        {
            _logger.LogError(ex, "The biography request was refused.");
            return new BiographyDraftResult(false, null, "The request was refused.");
        }
        catch (AnthropicIOException ex)
        {
            _logger.LogWarning(ex, "Could not reach the biography provider; this will be retried.");
            return new BiographyDraftResult(false, null, "Could not reach the provider. This will be tried again.");
        }
    }

    /// <summary>
    /// The instructions, which are mostly a list of things not to do.
    /// </summary>
    /// <remarks>
    /// Every prohibition here is a claim an agency would read as fact and act on. A
    /// biography that invents a campaign, a signing or an ambition is not a stylistic
    /// problem — it is a false statement about somebody's working life, published under
    /// the school's name.
    /// </remarks>
    private static string SystemPrompt(int targetWords) =>
        $"""
        You write short professional biographies for a modelling school's portfolio pages.
        An agency or casting director reads them.

        Use ONLY the facts given to you. This matters more than anything else here:

        - Do not invent experience, credits, campaigns, brands, agencies, publications,
          training, awards or representation. If you were not told it, it did not happen.
        - Do not invent personality, character, ambitions, interests or backstory. You have
          never met this person.
        - Do not describe their appearance beyond the measurements you were given, and do
          not comment on their attractiveness.
        - Do not guess at anything absent. Write around a missing fact; never fill it in.

        Write about {targetWords} words, in one or two paragraphs, in British English.
        Third person, present tense, plain and professional. No headings, no bullet points,
        no markdown, no preamble — return the biography text and nothing else.

        If the facts are too thin to fill {targetWords} words honestly, write something
        shorter. A short accurate biography is worth more than a padded one, and padding is
        where invention starts.

        Where the model is under 18, keep it plainly factual and appropriate for a minor:
        availability and measurements, nothing about their appearance or maturity.
        """;

    /// <summary>The facts, written out plainly so the absence of one is obvious.</summary>
    private static string Describe(BiographyFacts facts)
    {
        var text = new StringBuilder();

        text.AppendLine($"Name: {facts.Name}");
        text.AppendLine($"Category: {facts.ProfileType}");
        text.AppendLine(facts.Age is { } age ? $"Age: {age}" : "Age: not recorded");
        text.AppendLine(string.IsNullOrWhiteSpace(facts.Location)
            ? "Based: not recorded"
            : $"Based: {facts.Location}");

        if (facts.Measurements.Count == 0)
        {
            text.AppendLine("Measurements: none recorded");
        }
        else
        {
            text.AppendLine("Measurements:");

            foreach (var measurement in facts.Measurements)
            {
                text.AppendLine($"  {measurement.Label}: {measurement.Value}{measurement.Unit}");
            }
        }

        text.AppendLine($"Portfolio photographs: {facts.PhotographCount}");
        text.AppendLine($"Self-tape available: {(facts.HasSelfTape ? "yes" : "no")}");
        text.AppendLine();
        text.AppendLine("Write the biography.");

        return text.ToString();
    }
}
