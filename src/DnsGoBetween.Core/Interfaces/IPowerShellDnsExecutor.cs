using DnsGoBetween.Core.Models;

namespace DnsGoBetween.Core.Interfaces;

/// <summary>
/// Transport abstraction for DNS cmdlet execution.
/// v1: LocalPowerShellDnsExecutor  — runs cmdlets directly on the DNS server.
/// v2: RemotePowerShellDnsExecutor — runs cmdlets via PSSession (toggle via DnsOptions).
/// </summary>
public interface IDnsProvider
{
    string ProviderName { get; }
    Task<IReadOnlyList<DnsZone>> GetZonesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<DnsRecord>> GetResourceRecordsAsync(string zone, string? node, CancellationToken ct = default);
    Task AddResourceRecordAsync(AddRecordRequest request, CancellationToken ct = default);
    Task RemoveResourceRecordAsync(DeleteRecordRequest request, CancellationToken ct = default);
}
