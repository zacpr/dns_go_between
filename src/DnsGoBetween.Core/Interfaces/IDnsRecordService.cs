using DnsGoBetween.Core.Models;

namespace DnsGoBetween.Core.Interfaces;

/// <summary>
/// Application-facing DNS service. Validates inputs against policy before delegating to the executor.
/// </summary>
public interface IDnsRecordService
{
    IEnumerable<string> GetAvailableProviders();
    Task<IReadOnlyList<DnsZone>> ListZonesAsync(string provider = "Windows", CancellationToken ct = default);
    Task<IReadOnlyList<DnsRecord>> ListRecordsAsync(string provider, string zone, string? node = null, CancellationToken ct = default);
    Task AddRecordAsync(string provider, AddRecordRequest request, CancellationToken ct = default);
    Task DeleteRecordAsync(string provider, DeleteRecordRequest request, CancellationToken ct = default);
    bool CanWrite(System.Security.Claims.ClaimsPrincipal user, string provider);
}
