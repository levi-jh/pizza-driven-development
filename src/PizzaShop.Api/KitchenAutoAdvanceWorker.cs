namespace PizzaShop.Api;

/// <summary>
/// Optional background worker that periodically advances active orders through the kitchen lifecycle
/// when KITCHEN_AUTO_ADVANCE_SECONDS is configured.
/// </summary>
internal sealed class KitchenAutoAdvanceWorker : BackgroundService
{
    private readonly OrderStore _store;
    private readonly ILogger<KitchenAutoAdvanceWorker> _logger;
    private readonly TimeSpan _interval;

    public KitchenAutoAdvanceWorker(OrderStore store, ILogger<KitchenAutoAdvanceWorker> logger, TimeSpan interval)
    {
        _store = store;
        _logger = logger;
        _interval = interval;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Kitchen auto-advance timer started (Interval: {IntervalSeconds}s).", _interval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_interval, stoppingToken);

                var advanced = await _store.AdvanceAllActiveOrdersOneStepAsync(stoppingToken);
                if (advanced > 0)
                {
                    _logger.LogInformation("Kitchen timer advanced {Count} active order(s).", advanced);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error while auto-advancing kitchen orders. Will retry on next tick.");
            }
        }
    }
}
