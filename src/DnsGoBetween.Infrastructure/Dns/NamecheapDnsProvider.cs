using System.Xml.Linq;
using DnsGoBetween.Core.Interfaces;
using DnsGoBetween.Core.Models;
using Microsoft.Extensions.Configuration;

namespace DnsGoBetween.Infrastructure.Dns;

public sealed class NamecheapDnsProvider : IDnsProvider
{
    private readonly HttpClient _httpClient;
    private readonly string _apiUser;
    private readonly string _apiKey;
    private readonly string _clientIp;
    
    public string ProviderName => "Namecheap";

    public NamecheapDnsProvider(HttpClient httpClient, IConfiguration config)
    {
        _httpClient = httpClient;
        _apiUser = config.GetValue<string>("Dns:NamecheapApiUser") ?? throw new ArgumentException("NamecheapApiUser missing");
        _apiKey = config.GetValue<string>("Dns:NamecheapApiKey") ?? throw new ArgumentException("NamecheapApiKey missing");
        _clientIp = config.GetValue<string>("Dns:NamecheapClientIp") ?? throw new ArgumentException("NamecheapClientIp missing");
    }

    public async Task<IReadOnlyList<DnsZone>> GetZonesAsync(CancellationToken ct = default)
    {
        var url = $"?ApiUser={_apiUser}&ApiKey={_apiKey}&UserName={_apiUser}&ClientIp={_clientIp}&Command=namecheap.domains.getList";
        var response = await _httpClient.GetStringAsync(url, ct);
        var doc = XDocument.Parse(response);
        
        XNamespace ns = "http://api.namecheap.com/xml.response";
        return doc.Descendants(ns + "Domain")
            .Select(x => new DnsZone
            {
                Name = x.Attribute("Name")?.Value ?? string.Empty,
                ZoneType = "Primary",
                IsDynamicUpdateEnabled = true
            }).ToList();
    }

    public async Task<IReadOnlyList<DnsRecord>> GetResourceRecordsAsync(string zone, string? node, CancellationToken ct = default)
    {
        var (sld, tld) = SplitZone(zone);
        var url = $"?ApiUser={_apiUser}&ApiKey={_apiKey}&UserName={_apiUser}&ClientIp={_clientIp}&Command=namecheap.domains.dns.getHosts&SLD={sld}&TLD={tld}";
        
        var response = await _httpClient.GetStringAsync(url, ct);
        var doc = XDocument.Parse(response);
        XNamespace ns = "http://api.namecheap.com/xml.response";

        var records = doc.Descendants(ns + "host")
            .Where(x => Enum.TryParse<DnsRecordType>(x.Attribute("Type")?.Value, true, out _))
            .Select(x => new DnsRecord
            {
                HostName = x.Attribute("Name")?.Value ?? "@",
                ZoneName = zone,
                RecordType = Enum.Parse<DnsRecordType>(x.Attribute("Type")?.Value ?? "A", true),
                Data = x.Attribute("Address")?.Value ?? string.Empty,
                TimeToLive = int.TryParse(x.Attribute("TTL")?.Value, out var ttl) ? ttl : 1800
            }).ToList();

        if (!string.IsNullOrEmpty(node))
        {
            records = records.Where(r => string.Equals(r.HostName, node, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        return records;
    }

    public async Task AddResourceRecordAsync(AddRecordRequest request, CancellationToken ct = default)
    {
        var records = await GetResourceRecordsAsync(request.ZoneName, null, ct);
        var updatedRecords = records.ToList();
        
        updatedRecords.Add(new DnsRecord
        {
            HostName = request.HostName,
            ZoneName = request.ZoneName,
            RecordType = request.RecordType,
            Data = request.Data,
            TimeToLive = request.TimeToLive
        });

        await SetHostsAsync(request.ZoneName, updatedRecords, ct);
    }

    public async Task RemoveResourceRecordAsync(DeleteRecordRequest request, CancellationToken ct = default)
    {
        var records = await GetResourceRecordsAsync(request.ZoneName, null, ct);
        var updatedRecords = records.ToList();

        var target = updatedRecords.FirstOrDefault(r => 
            string.Equals(r.HostName, request.HostName, StringComparison.OrdinalIgnoreCase) && 
            r.RecordType == request.RecordType && 
            string.Equals(r.Data, request.Data, StringComparison.OrdinalIgnoreCase));

        if (target is null)
            throw new InvalidOperationException("No exact DNS record match found for deletion.");

        updatedRecords.Remove(target);
        await SetHostsAsync(request.ZoneName, updatedRecords, ct);
    }

    private async Task SetHostsAsync(string zone, List<DnsRecord> records, CancellationToken ct)
    {
        var (sld, tld) = SplitZone(zone);
        var contentArgs = new List<KeyValuePair<string, string>>
        {
            new("ApiUser", _apiUser),
            new("ApiKey", _apiKey),
            new("UserName", _apiUser),
            new("ClientIp", _clientIp),
            new("Command", "namecheap.domains.dns.setHosts"),
            new("SLD", sld),
            new("TLD", tld)
        };

        for (var i = 0; i < records.Count; i++)
        {
            var index = i + 1;
            contentArgs.Add(new($"HostName{index}", records[i].HostName));
            contentArgs.Add(new($"RecordType{index}", records[i].RecordType.ToString()));
            contentArgs.Add(new($"Address{index}", records[i].Data));
            contentArgs.Add(new($"TTL{index}", records[i].TimeToLive.ToString()));
        }

        var response = await _httpClient.PostAsync("", new FormUrlEncodedContent(contentArgs), ct);
        response.EnsureSuccessStatusCode();
        
        var resultXml = await response.Content.ReadAsStringAsync(ct);
        if (resultXml.Contains("Status=\"ERROR\""))
        {
            throw new InvalidOperationException("Namecheap API rejected the record update.");
        }
    }

    private (string sld, string tld) SplitZone(string zone)
    {
        var lastDot = zone.LastIndexOf('.');
        return lastDot < 0 ? (zone, "") : (zone[..lastDot], zone[(lastDot + 1)..]);
    }
}