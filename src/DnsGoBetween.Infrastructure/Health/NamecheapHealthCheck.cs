using DnsGoBetween.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace DnsGoBetween.Infrastructure.Health;

public sealed class NamecheapHealthCheck : IHealthCheck
{
    private readonly IServiceScopeFactory _scopeFactory;

    public NamecheapHealthCheck(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var providers = scope.ServiceProvider.GetRequiredService<IEnumerable<IDnsProvider>>();
        var ncProvider = providers.FirstOrDefault(p => p.ProviderName == "Namecheap");
        
        if (ncProvider is null)
        {
            return HealthCheckResult.Healthy("Namecheap provider is not enabled.");
        }

        try
        {
            await ncProvider.GetZonesAsync(cancellationToken);
            return HealthCheckResult.Healthy("Namecheap API is reachable and authenticated.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy($"Namecheap API check failed: {ex.Message}", ex);
        }
    }
}