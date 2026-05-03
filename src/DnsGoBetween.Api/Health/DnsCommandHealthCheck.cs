using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace DnsGoBetween.Api.Health;

/// <summary>
/// Returns cached readiness status produced by DnsReadinessProbeService.
/// </summary>
public sealed class DnsCommandHealthCheck : IHealthCheck
{
    private readonly DnsReadinessState _state;

    public DnsCommandHealthCheck(
        DnsReadinessState state)
    {
        _state = state;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;

        var snapshot = _state.GetSnapshot();
        if (snapshot.LastCheckUtc is null)
        {
            return HealthCheckResult.Unhealthy("Readiness probe has not completed yet.");
        }

        var data = new Dictionary<string, object>
        {
            ["lastCheckUtc"] = snapshot.LastCheckUtc?.ToString("O") ?? string.Empty,
            ["lastSuccessUtc"] = snapshot.LastSuccessUtc?.ToString("O") ?? string.Empty,
            ["lastError"] = snapshot.LastError ?? string.Empty
        };

        if (snapshot.IsReady)
        {
            return HealthCheckResult.Healthy("DNS readiness is healthy (cached).", data);
        }

        return HealthCheckResult.Unhealthy("DNS readiness is unhealthy (cached).", data: data);
    }
}
