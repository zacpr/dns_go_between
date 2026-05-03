using DnsGoBetween.Core.Models;

namespace DnsGoBetween.Core.Interfaces;

/// <summary>
/// Application-facing DNS service. Validates inputs against policy before delegating to the executor.
/// </summary>
public interface IDnsRecordService
{
    Task<IReadOnlyList<DnsZone>> ListZonesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<DnsRecord>> ListRecordsAsync(string zone, string? node = null, CancellationToken ct = default);
    Task AddRecordAsync(AddRecordRequest request, CancellationToken ct = default);
    Task DeleteRecordAsync(DeleteRecordRequest request, CancellationToken ct = default);
}
