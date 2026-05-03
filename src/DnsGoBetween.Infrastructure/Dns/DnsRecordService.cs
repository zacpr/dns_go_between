using System.Net;
using System.Net.Sockets;
using DnsGoBetween.Core.Configuration;
using DnsGoBetween.Core.Interfaces;
using DnsGoBetween.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DnsGoBetween.Infrastructure.Dns;

public sealed class DnsRecordService : IDnsRecordService
{
    private readonly IPowerShellDnsExecutor _executor;
    private readonly DnsOptions _options;
    private readonly ILogger<DnsRecordService> _logger;

    public DnsRecordService(
        IPowerShellDnsExecutor executor,
        IOptions<DnsOptions> options,
        ILogger<DnsRecordService> logger)
    {
        _executor = executor;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<IReadOnlyList<DnsZone>> ListZonesAsync(CancellationToken ct = default)
    {
        var zones = await _executor.GetZonesAsync(ct);

        if (_options.AllowedZones.Length > 0)
            zones = zones
                .Where(z => _options.AllowedZones.Contains(z.Name, StringComparer.OrdinalIgnoreCase))
                .ToList();

        return zones;
    }

    public Task<IReadOnlyList<DnsRecord>> ListRecordsAsync(
        string zone, string? node = null, CancellationToken ct = default)
    {
        ValidateZone(zone);
        return _executor.GetResourceRecordsAsync(zone, node, ct);
    }

    public Task AddRecordAsync(AddRecordRequest request, CancellationToken ct = default)
    {
        ValidateZone(request.ZoneName);
        ValidateRecordType(request.RecordType);
        ValidateHostName(
            request.HostName,
            allowWildcard: request.RecordType == DnsRecordType.A,
            allowUnderscore: request.RecordType == DnsRecordType.TXT);
        ValidateRecordData(request.RecordType, request.Data);
        return _executor.AddResourceRecordAsync(request, ct);
    }

    public Task DeleteRecordAsync(DeleteRecordRequest request, CancellationToken ct = default)
    {
        ValidateZone(request.ZoneName);
        ValidateRecordType(request.RecordType);
        ValidateHostName(
            request.HostName,
            allowWildcard: request.RecordType == DnsRecordType.A,
            allowUnderscore: request.RecordType == DnsRecordType.TXT);
        return _executor.RemoveResourceRecordAsync(request, ct);
    }

    // ── Validation ────────────────────────────────────────────────────────────

    private void ValidateZone(string zone)
    {
        if (_options.AllowedZones.Length > 0 &&
            !_options.AllowedZones.Contains(zone, StringComparer.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException(
                $"Zone '{zone}' is not in the allowed zones list.");
    }

    private void ValidateRecordType(DnsRecordType type)
    {
        var typeName = type.ToString();
        if (_options.AllowedRecordTypes.Length > 0 &&
            !_options.AllowedRecordTypes.Contains(typeName, StringComparer.OrdinalIgnoreCase))
            throw new NotSupportedException($"Record type '{typeName}' is not allowed.");
    }

    private static void ValidateHostName(
        string hostName,
        bool allowWildcard = false,
        bool allowUnderscore = false)
    {
        if (string.IsNullOrWhiteSpace(hostName))
            throw new ArgumentException("Host name must not be empty.", nameof(hostName));

        if (hostName == "@") return; // zone apex
        if (allowWildcard && hostName == "*") return;

        foreach (var label in hostName.Split('.'))
        {
            if (label.Length == 0)
                throw new ArgumentException($"Invalid hostname: '{hostName}'.");
            if (!label.All(c => char.IsLetterOrDigit(c) || c == '-' || (allowUnderscore && c == '_')))
                throw new ArgumentException(
                    $"Invalid character in hostname label '{label}' in '{hostName}'.");
            if (label.StartsWith('-') || label.EndsWith('-'))
                throw new ArgumentException(
                    $"Hostname label '{label}' cannot start or end with a hyphen.");
        }
    }

    private static void ValidateRecordData(DnsRecordType type, string data)
    {
        switch (type)
        {
            case DnsRecordType.A:
                if (!IPAddress.TryParse(data, out var v4) ||
                    v4.AddressFamily != AddressFamily.InterNetwork)
                    throw new ArgumentException($"Invalid IPv4 address: '{data}'.");
                break;

            case DnsRecordType.AAAA:
                if (!IPAddress.TryParse(data, out var v6) ||
                    v6.AddressFamily != AddressFamily.InterNetworkV6)
                    throw new ArgumentException($"Invalid IPv6 address: '{data}'.");
                break;

            case DnsRecordType.CNAME:
            case DnsRecordType.PTR:
            case DnsRecordType.TXT:
                if (string.IsNullOrWhiteSpace(data))
                    throw new ArgumentException(
                        $"Record data cannot be empty for {type} records.");
                break;
        }
    }
}
