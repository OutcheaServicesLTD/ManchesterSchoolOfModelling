namespace Msm.Portfolio.Web.Services;

/// <summary>
/// Pushes outstanding portfolio state to the CRM (specification section 45).
/// </summary>
/// <remarks>
/// <para>
/// The push happens here, on a timer, rather than inside the operation that caused it.
/// That is what makes the specification's rule hold: publishing a portfolio marks it as
/// needing a sync and then finishes, so a CRM that is slow or down cannot delay the
/// studio, and cannot roll back a purchase that already succeeded.
/// </para>
/// <para>
/// Failures are retried with a backoff held on the row, so a restart during a CRM
/// outage does not reset it and immediately hammer a service that is already
/// struggling.
/// </para>
/// </remarks>
public class CrmSyncWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<CrmSyncWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(2);

    /// <summary>Lets startup, migration and seeding finish before the first pass.</summary>
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("CRM sync worker started; checking every {Interval}.", Interval);

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

        logger.LogInformation("CRM sync worker stopped.");
    }

    private async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var sync = scope.ServiceProvider.GetRequiredService<ICrmSyncService>();

            var summary = await sync.SyncPendingAsync(cancellationToken);

            if (summary.Attempted > 0)
            {
                logger.LogInformation(
                    "CRM sync: {Succeeded} succeeded, {Failed} failed, {Skipped} skipped of {Attempted}.",
                    summary.Succeeded, summary.Failed, summary.Skipped, summary.Attempted);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutting down.
        }
        catch (Exception ex)
        {
            // One bad pass must not stop the worker, or every later change would stop
            // reaching the CRM with nothing to indicate why.
            logger.LogError(ex, "The CRM sync pass failed; it will run again shortly.");
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
