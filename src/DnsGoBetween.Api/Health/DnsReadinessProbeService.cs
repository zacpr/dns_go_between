using DnsGoBetween.Core.Interfaces;

namespace DnsGoBetween.Api.Health;

public sealed class DnsReadinessProbeService : BackgroundService
{
    private static readonly TimeSpan ProbeInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly DnsReadinessState _state;
    private readonly ILogger<DnsReadinessProbeService> _logger;

    public DnsReadinessProbeService(
        IServiceScopeFactory scopeFactory,
        DnsReadinessState state,
        ILogger<DnsReadinessProbeService> logger)
    {
        _scopeFactory = scopeFactory;
        _state = state;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await ProbeOnceAsync(stoppingToken);

            try
            {
                await Task.Delay(ProbeInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task ProbeOnceAsync(CancellationToken stoppingToken)
    {
        var nowUtc = DateTimeOffset.UtcNow;

        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var dns = scope.ServiceProvider.GetRequiredService<IDnsRecordService>();

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            cts.CancelAfter(ProbeTimeout);

            await dns.ListZonesAsync("Windows", cts.Token);
            _state.ReportSuccess(nowUtc);
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            _state.ReportFailure(nowUtc, ex.Message);
            _logger.LogError(ex, "Background DNS readiness probe failed.");
        }
    }
}