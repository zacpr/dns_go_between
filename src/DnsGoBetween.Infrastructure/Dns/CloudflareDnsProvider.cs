using System.Net.Http.Json;
using DnsGoBetween.Core.Interfaces;
using DnsGoBetween.Core.Models;

namespace DnsGoBetween.Infrastructure.Dns;

public sealed class CloudflareDnsProvider : IDnsProvider
{
    private readonly HttpClient _httpClient;
    
    public string ProviderName => "Cloudflare";

    public CloudflareDnsProvider(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<IReadOnlyList<DnsZone>> GetZonesAsync(CancellationToken ct = default)
    {
        var response = await _httpClient.GetFromJsonAsync<CloudflareZoneResponse>("zones", ct);
        return response?.Result?.Select(z => new DnsZone
        {
            Name = z.Name,
            ZoneType = "Primary",
            IsDynamicUpdateEnabled = true
        }).ToList() ?? new List<DnsZone>();
    }

    public async Task<IReadOnlyList<DnsRecord>> GetResourceRecordsAsync(string zone, string? node, CancellationToken ct = default)
    {
        var zoneId = await GetZoneIdAsync(zone, ct);
        if (string.IsNullOrEmpty(zoneId)) return Array.Empty<DnsRecord>();

        var url = $"zones/{zoneId}/dns_records";
        if (!string.IsNullOrEmpty(node))
        {
            var fqdn = node == "@" ? zone : $"{node}.{zone}";
            url += $"?name={Uri.EscapeDataString(fqdn)}";
        }

        var response = await _httpClient.GetFromJsonAsync<CloudflareRecordResponse>(url, ct);
        return response?.Result?.Select(r => new DnsRecord
        {
            HostName = r.Name.Replace($".{zone}", "").Replace(zone, "@"),
            ZoneName = zone,
            RecordType = Enum.Parse<DnsRecordType>(r.Type, true),
            Data = r.Content,
            TimeToLive = r.Ttl
        }).ToList() ?? new List<DnsRecord>();
    }

    public async Task AddResourceRecordAsync(AddRecordRequest request, CancellationToken ct = default)
    {
        var zoneId = await GetZoneIdAsync(request.ZoneName, ct);
        var fqdn = request.HostName == "@" ? request.ZoneName : $"{request.HostName}.{request.ZoneName}";
        
        var payload = new 
        {
            type = request.RecordType.ToString(),
            name = fqdn,
            content = request.Data,
            ttl = request.TimeToLive
        };

        var response = await _httpClient.PostAsJsonAsync($"zones/{zoneId}/dns_records", payload, ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task RemoveResourceRecordAsync(DeleteRecordRequest request, CancellationToken ct = default)
    {
        var zoneId = await GetZoneIdAsync(request.ZoneName, ct);
        var fqdn = request.HostName == "@" ? request.ZoneName : $"{request.HostName}.{request.ZoneName}";
        
        var response = await _httpClient.GetFromJsonAsync<CloudflareRecordResponse>($"zones/{zoneId}/dns_records?name={Uri.EscapeDataString(fqdn)}&type={request.RecordType}", ct);
        
        var targetRecord = response?.Result?.FirstOrDefault(r => r.Content == request.Data);
        if (targetRecord is null) 
            throw new InvalidOperationException("No exact DNS record match found for deletion.");

        var deleteResponse = await _httpClient.DeleteAsync($"zones/{zoneId}/dns_records/{targetRecord.Id}", ct);
        deleteResponse.EnsureSuccessStatusCode();
    }

    private async Task<string?> GetZoneIdAsync(string zoneName, CancellationToken ct)
    {
        var response = await _httpClient.GetFromJsonAsync<CloudflareZoneResponse>($"zones?name={Uri.EscapeDataString(zoneName)}", ct);
        return response?.Result?.FirstOrDefault()?.Id;
    }

    private class CloudflareZoneResponse { public List<CfZone>? Result { get; set; } }
    private class CfZone { public string Id { get; set; } = ""; public string Name { get; set; } = ""; }
    private class CloudflareRecordResponse { public List<CfRecord>? Result { get; set; } }
    private class CfRecord { public string Id { get; set; } = ""; public string Type { get; set; } = ""; public string Name { get; set; } = ""; public string Content { get; set; } = ""; public int Ttl { get; set; } }
}