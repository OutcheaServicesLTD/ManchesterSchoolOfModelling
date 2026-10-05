using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace Msm.Portfolio.Web.Configuration;

/// <summary>
/// Rate limit policy names, applied per endpoint (specification section 43).
/// </summary>
public static class RateLimitPolicies
{
    /// <summary>Sign-in. The tightest limit, because it guards every staff account.</summary>
    public const string SignIn = "sign-in";

    /// <summary>Anonymous forms: onboarding and guardian approval.</summary>
    public const string AnonymousForm = "anonymous-form";

    /// <summary>The public enquiry form, which anyone on the internet can reach.</summary>
    public const string PublicEnquiry = "public-enquiry";

    /// <summary>Inbound provider webhooks.</summary>
    public const string Webhook = "webhook";
}

public static class RateLimiting
{
    /// <summary>
    /// Adds the limits from specification section 43.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Sized against how the application is genuinely used rather than as low as
    /// possible. A studio onboarding a queue of clients after a shoot, or an agency
    /// browsing several portfolios, must never hit these; they exist to stop automated
    /// abuse, not to ration normal work.
    /// </para>
    /// <para>
    /// Limits are keyed by client IP. Behind a proxy or load balancer this needs
    /// forwarded headers configured, or every request appears to come from one address
    /// and the limit becomes global — noted in the deployment documentation.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddMsmRateLimiting(this IServiceCollection services)
    {
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            options.OnRejected = async (context, cancellationToken) =>
            {
                // Retry-After so a well-behaved client knows to wait rather than
                // hammering the endpoint harder.
                context.HttpContext.Response.Headers.RetryAfter = "60";

                var logger = context.HttpContext.RequestServices
                    .GetRequiredService<ILoggerFactory>()
                    .CreateLogger("RateLimiting");

                logger.LogWarning(
                    "Rate limit reached for {Path} from {Address}.",
                    context.HttpContext.Request.Path,
                    context.HttpContext.Connection.RemoteIpAddress);

                if (!context.HttpContext.Response.HasStarted)
                {
                    await context.HttpContext.Response.WriteAsync(
                        "Too many requests. Please wait a moment and try again.", cancellationToken);
                }
            };

            // Sign-in: brute force is already throttled by Identity's account lockout,
            // but lockout is per account. This limits an attacker working through many
            // accounts from one source.
            options.AddPolicy(RateLimitPolicies.SignIn, PartitionByAddress(
                permitLimit: 10, window: TimeSpan.FromMinutes(5)));

            // Onboarding and guardian approval. Generous, because a studio may onboard
            // several clients in quick succession from the same network.
            options.AddPolicy(RateLimitPolicies.AnonymousForm, PartitionByAddress(
                permitLimit: 30, window: TimeSpan.FromMinutes(10)));

            // The enquiry form is the one endpoint that writes to the database from a
            // completely anonymous request, so it is the most attractive to abuse.
            options.AddPolicy(RateLimitPolicies.PublicEnquiry, PartitionByAddress(
                permitLimit: 5, window: TimeSpan.FromMinutes(10)));

            // Webhooks arrive in bursts when a provider retries a backlog, so this is
            // high: it exists to bound a flood, not to shape normal delivery.
            options.AddPolicy(RateLimitPolicies.Webhook, PartitionByAddress(
                permitLimit: 300, window: TimeSpan.FromMinutes(1)));
        });

        return services;
    }

    /// <summary>
    /// A fixed window per client address.
    /// </summary>
    /// <remarks>
    /// Requests over the limit are rejected rather than queued. Queuing would hold
    /// connections open under exactly the load this is meant to shed.
    /// </remarks>
    private static Func<HttpContext, RateLimitPartition<string>> PartitionByAddress(
        int permitLimit, TimeSpan window) =>
        context => RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = permitLimit,
                Window = window,
                QueueLimit = 0,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst
            });
}
