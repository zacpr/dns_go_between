using DnsGoBetween.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace DnsGoBetween.Infrastructure.Health;

public sealed class CloudflareHealthCheck : IHealthCheck
{
    private readonly IServiceScopeFactory _scopeFactory;

    public CloudflareHealthCheck(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var providers = scope.ServiceProvider.GetRequiredService<IEnumerable<IDnsProvider>>();
        var cfProvider = providers.FirstOrDefault(p => p.ProviderName == "Cloudflare");
        
        if (cfProvider is null)
        {
            return HealthCheckResult.Healthy("Cloudflare provider is not enabled.");
        }

        try
        {
            await cfProvider.GetZonesAsync(cancellationToken);
            return HealthCheckResult.Healthy("Cloudflare API is reachable and authenticated.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy($"Cloudflare API check failed: {ex.Message}", ex);
        }
    }
}