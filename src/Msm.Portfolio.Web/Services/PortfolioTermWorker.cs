namespace Msm.Portfolio.Web.Services;

/// <summary>
/// Takes down portfolios whose purchased year has ended.
/// </summary>
/// <remarks>
/// <para>
/// A year runs out by the passage of time, not by anyone doing anything, so nothing in a
/// request can be relied on to notice. This runs on a timer instead, which means a
/// portfolio comes down on the day even if no staff member signs in and the model never
/// returns.
/// </para>
/// <para>
/// Idempotent by construction: the query only matches published portfolios whose expiry
/// has passed, and taking one down moves it out of that set. A missed run catches up on
/// the next one, and a duplicate run does nothing.
/// </para>
/// </remarks>
public class PortfolioTermWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<PortfolioTermWorker> logger) : BackgroundService
{
    /// <summary>
    /// Hourly. The term is measured in days, so this is far finer than needed and keeps
    /// the work small; there is no benefit to checking more often.
    /// </summary>
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    /// <summary>
    /// A short delay before the first run so startup, migration and seeding finish first,
    /// rather than competing with them.
    /// </summary>
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Portfolio term worker started; checking every {Interval}.", Interval);

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

        logger.LogInformation("Portfolio term worker stopped.");
    }

    private async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            // A new scope per run: the data context is scoped, and holding one for the
            // lifetime of the application would accumulate tracked entities forever.
            using var scope = scopeFactory.CreateScope();
            var terms = scope.ServiceProvider.GetRequiredService<IPortfolioTermService>();

            var unpublished = await terms.ExpireElapsedTermsAsync(cancellationToken);

            if (unpublished > 0)
            {
                logger.LogInformation(
                    "{Count} portfolio(s) were taken down because the purchased year ended.", unpublished);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutting down.
        }
        catch (Exception ex)
        {
            // One bad run must not kill the worker, or every later term would silently
            // stop expiring and portfolios would stay live past what was paid for.
            logger.LogError(ex, "The portfolio term check failed; it will run again shortly.");
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
