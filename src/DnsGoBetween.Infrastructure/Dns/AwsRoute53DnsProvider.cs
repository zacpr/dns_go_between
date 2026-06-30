using Amazon.Route53;
using Amazon.Route53.Model;
using Amazon.Runtime;
using DnsGoBetween.Core.Interfaces;
using DnsGoBetween.Core.Models;
using Microsoft.Extensions.Configuration;

namespace DnsGoBetween.Infrastructure.Dns;

public sealed class AwsRoute53DnsProvider : IDnsProvider
{
    private readonly IAmazonRoute53 _client;

    public string ProviderName => "Route53";

    public AwsRoute53DnsProvider(IConfiguration config)
        : this(CreateClient(config))
    {
    }

    // Internal ctor for testing — bypasses AWS credential construction so a mock
    // IAmazonRoute53 can be injected. Production code uses the IConfiguration ctor.
    internal AwsRoute53DnsProvider(IAmazonRoute53 client)
    {
        _client = client;
    }

    private static IAmazonRoute53 CreateClient(IConfiguration config)
    {
        var accessKey = config.GetValue<string>("Dns:AwsAccessKey");
        var secretKey = config.GetValue<string>("Dns:AwsSecretKey");
        var region = config.GetValue<string>("Dns:AwsRegion") ?? "us-east-1";

        var credentials = new BasicAWSCredentials(accessKey, secretKey);
        return new AmazonRoute53Client(credentials, Amazon.RegionEndpoint.GetBySystemName(region));
    }

    public async Task<IReadOnlyList<DnsZone>> GetZonesAsync(CancellationToken ct = default)
    {
        var response = await _client.ListHostedZonesAsync(new ListHostedZonesRequest(), ct);
        return response.HostedZones.Select(z => new DnsZone
        {
            Name = z.Name.TrimEnd('.'),
            ZoneType = "Primary",
            IsDynamicUpdateEnabled = true
        }).ToList();
    }

    public async Task<IReadOnlyList<DnsRecord>> GetResourceRecordsAsync(string zone, string? node, CancellationToken ct = default)
    {
        var zoneId = await GetZoneIdAsync(zone, ct);
        if (string.IsNullOrEmpty(zoneId)) return Array.Empty<DnsRecord>();

        var request = new ListResourceRecordSetsRequest { HostedZoneId = zoneId };
        if (!string.IsNullOrEmpty(node))
        {
            request.StartRecordName = node == "@" ? zone : $"{node}.{zone}";
        }

        var response = await _client.ListResourceRecordSetsAsync(request, ct);
        return response.ResourceRecordSets
            .Where(r => Enum.TryParse<DnsRecordType>(r.Type, out _)) // Filter to supported types
            .SelectMany(r => r.ResourceRecords.Select(val => new DnsRecord
            {
                HostName = r.Name.TrimEnd('.').Replace($".{zone}", "").Replace(zone, "@"),
                ZoneName = zone,
                RecordType = Enum.Parse<DnsRecordType>(r.Type, true),
                Data = val.Value.Trim('"'), // Route53 wraps TXT in quotes
                TimeToLive = (int)r.TTL
            })).ToList();
    }

    public async Task AddResourceRecordAsync(AddRecordRequest request, CancellationToken ct = default)
    {
        var zoneId = await GetZoneIdAsync(request.ZoneName, ct);
        var fqdn = request.HostName == "@" ? request.ZoneName : $"{request.HostName}.{request.ZoneName}";
        var value = request.RecordType == DnsRecordType.TXT ? $"\"{request.Data}\"" : request.Data;

        var changeBatch = new ChangeBatch
        {
            Changes = new List<Change> {
                new Change {
                    Action = ChangeAction.UPSERT,
                    ResourceRecordSet = new ResourceRecordSet {
                        Name = fqdn,
                        Type = request.RecordType.ToString(),
                        TTL = request.TimeToLive,
                        ResourceRecords = new List<ResourceRecord> { new ResourceRecord { Value = value } }
                    }
                }
            }
        };

        await _client.ChangeResourceRecordSetsAsync(new ChangeResourceRecordSetsRequest(zoneId, changeBatch), ct);
    }

    public async Task RemoveResourceRecordAsync(DeleteRecordRequest request, CancellationToken ct = default)
    {
        var zoneId = await GetZoneIdAsync(request.ZoneName, ct);
        var fqdn = request.HostName == "@" ? request.ZoneName : $"{request.HostName}.{request.ZoneName}";
        var value = request.RecordType == DnsRecordType.TXT ? $"\"{request.Data}\"" : request.Data;

        var changeBatch = new ChangeBatch
        {
            Changes = new List<Change> {
                new Change {
                    Action = ChangeAction.DELETE,
                    ResourceRecordSet = new ResourceRecordSet {
                        Name = fqdn,
                        Type = request.RecordType.ToString(),
                        TTL = 3600, // TTL doesn't strictly matter for DELETE in Route53 but SDK requires it
                        ResourceRecords = new List<ResourceRecord> { new ResourceRecord { Value = value } }
                    }
                }
            }
        };

        await _client.ChangeResourceRecordSetsAsync(new ChangeResourceRecordSetsRequest(zoneId, changeBatch), ct);
    }

    private async Task<string?> GetZoneIdAsync(string zoneName, CancellationToken ct)
    {
        var response = await _client.ListHostedZonesByNameAsync(new ListHostedZonesByNameRequest { DNSName = zoneName }, ct);
        return response.HostedZones.FirstOrDefault(z => z.Name.TrimEnd('.').Equals(zoneName, StringComparison.OrdinalIgnoreCase))?.Id;
    }
}