namespace Msm.Portfolio.Web.Services;

/// <summary>
/// Writes the biography drafts that approvals have asked for.
/// </summary>
/// <remarks>
/// On a timer, for the same reason the CRM sync is: approving a portfolio is the
/// administrator's action and finishes on its own. A provider that is slow or down
/// delays a suggestion, never an approval.
/// </remarks>
public class BiographyDraftWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<BiographyDraftWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    /// <summary>Lets startup, migration and seeding finish before the first pass.</summary>
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Biography draft worker started; checking every {Interval}.", Interval);

        try
        {
            await Task.Delay(StartupDelay, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        using var timer = new PeriodicTimer(Interval);

        do
        {
            await RunOnceAsync(stoppingToken);
        }
        while (await SafeWaitAsync(timer, stoppingToken));

        logger.LogInformation("Biography draft worker stopped.");
    }

    private async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var drafts = scope.ServiceProvider.GetRequiredService<IBiographyDraftService>();

            var summary = await drafts.WritePendingAsync(cancellationToken);

            if (summary.Total > 0)
            {
                logger.LogInformation(
                    "Biography drafts: {Succeeded} written, {Failed} failed of {Total}.",
                    summary.Succeeded, summary.Failed, summary.Total);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        catch (Exception ex)
        {
            // A failed pass must never take the worker down with it.
            logger.LogError(ex, "The biography draft pass failed; it will run again shortly.");
        }
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken cancellationToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
