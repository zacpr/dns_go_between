using DnsGoBetween.Core.Interfaces;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace DnsGoBetween.Api.Health;

/// <summary>
/// Verifies that the DNS Server PowerShell cmdlets are accessible. Used by /health/ready.
/// </summary>
public sealed class DnsCommandHealthCheck : IHealthCheck
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DnsCommandHealthCheck> _logger;

    public DnsCommandHealthCheck(
        IServiceScopeFactory scopeFactory,
        ILogger<DnsCommandHealthCheck> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var dns = scope.ServiceProvider.GetRequiredService<IDnsRecordService>();

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(10));

            await dns.ListZonesAsync(cts.Token);
            return HealthCheckResult.Healthy("DNS cmdlets are accessible.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DNS readiness check failed.");
            return HealthCheckResult.Unhealthy("DNS cmdlets unavailable.", ex);
        }
    }
}
