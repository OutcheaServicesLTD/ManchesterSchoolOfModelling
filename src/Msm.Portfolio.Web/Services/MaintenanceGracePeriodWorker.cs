namespace Msm.Portfolio.Web.Services;

/// <summary>
/// Takes down portfolios whose maintenance grace period has run out
/// (specification section 23).
/// </summary>
/// <remarks>
/// <para>
/// A grace period expires by the passage of time, not by anyone doing anything, so
/// nothing in a request can be relied on to notice. This runs on a timer instead, which
/// means a portfolio comes down on the seventh day even if no staff member signs in and
/// the client never returns.
/// </para>
/// <para>
/// It is deliberately idempotent: the query only matches subscriptions still in
/// PaymentIssue whose deadline has passed, and expiring one moves it out of that set. A
/// missed run therefore catches up on the next one rather than losing the work, and a
/// duplicate run does nothing.
/// </para>
/// </remarks>
public class MaintenanceGracePeriodWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<MaintenanceGracePeriodWorker> logger) : BackgroundService
{
    /// <summary>
    /// Hourly. The deadline is measured in days, so this is far finer than needed and
    /// keeps the work small; there is no benefit to checking more often.
    /// </summary>
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    /// <summary>
    /// A short delay before the first run so startup, migration and seeding finish
    /// first, rather than competing with them.
    /// </summary>
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Maintenance grace period worker started; checking every {Interval}.", Interval);

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

        logger.LogInformation("Maintenance grace period worker stopped.");
    }

    private async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            // A new scope per run: the data context is scoped, and holding one for the
            // lifetime of the application would accumulate tracked entities forever.
            using var scope = scopeFactory.CreateScope();
            var maintenance = scope.ServiceProvider.GetRequiredService<IMaintenanceService>();

            var unpublished = await maintenance.ExpireElapsedGracePeriodsAsync(cancellationToken);

            if (unpublished > 0)
            {
                logger.LogWarning(
                    "{Count} portfolio(s) were taken down for unresolved maintenance payments.", unpublished);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutting down.
        }
        catch (Exception ex)
        {
            // One bad run must not kill the worker, or every later grace period would
            // silently stop expiring and portfolios would stay live without payment.
            logger.LogError(ex, "The maintenance grace period check failed; it will run again shortly.");
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
