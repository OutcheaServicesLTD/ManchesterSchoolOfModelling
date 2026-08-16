namespace Msm.Portfolio.Web.Services;

/// <summary>
/// Sends an outbound message to someone who has no account, which today means a
/// guardian receiving an approval request (specification section 11).
/// </summary>
/// <remarks>
/// Client-facing messaging is intended to run through GoHighLevel's existing
/// automation (specification section 37), which arrives in Phase 9. This abstraction
/// exists so the guardian workflow is complete now and the delivery mechanism can be
/// swapped without touching it.
/// </remarks>
public interface IEmailSender
{
    Task SendAsync(string toEmail, string subject, string body, CancellationToken cancellationToken = default);
}

/// <summary>
/// Writes the message to the log instead of delivering it. Lets the guardian flow be
/// exercised end to end in development by copying the approval link from the log.
/// </summary>
public class LoggingEmailSender(ILogger<LoggingEmailSender> logger, IHostEnvironment environment) : IEmailSender
{
    public Task SendAsync(
        string toEmail,
        string subject,
        string body,
        CancellationToken cancellationToken = default)
    {
        if (!environment.IsDevelopment())
        {
            // Outside development this silently drops real messages, which would mean a
            // guardian never receives their approval request. Loud enough to be noticed.
            logger.LogError(
                "No email provider is configured. The message '{Subject}' to {Email} was NOT delivered. "
                + "Configure a sender before relying on the guardian consent workflow.",
                subject, toEmail);

            return Task.CompletedTask;
        }

        logger.LogInformation(
            "Email (not sent, development only)\n  To: {Email}\n  Subject: {Subject}\n{Body}",
            toEmail, subject, body);

        return Task.CompletedTask;
    }
}
