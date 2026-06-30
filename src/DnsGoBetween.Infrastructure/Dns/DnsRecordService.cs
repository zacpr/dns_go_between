using System.Net;
using System.Net.Sockets;
using DnsGoBetween.Core.Configuration;
using DnsGoBetween.Core.Interfaces;
using DnsGoBetween.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using System.Security.Claims;
using Microsoft.Extensions.Options;

namespace DnsGoBetween.Infrastructure.Dns;

public sealed class DnsRecordService : IDnsRecordService
{
    private readonly IEnumerable<IDnsProvider> _providers;
    private readonly DnsOptions _options;
    private readonly ILogger<DnsRecordService> _logger;
    private readonly IConfiguration _config;

    public DnsRecordService(
        IEnumerable<IDnsProvider> providers,
        IOptions<DnsOptions> options,
        ILogger<DnsRecordService> logger,
        IConfiguration config)
    {
        _providers = providers;
        _options = options.Value;
        _logger = logger;
        _config = config;
    }
    
    private IDnsProvider GetProvider(string providerName)
    {
        var provider = _providers.FirstOrDefault(p => p.ProviderName.Equals(providerName, StringComparison.OrdinalIgnoreCase));
        if (provider is null)
            throw new ArgumentException($"DNS Provider '{providerName}' is not configured or found.");
        return provider;
    }

    public IEnumerable<string> GetAvailableProviders() => _providers.Select(p => p.ProviderName);

    public bool CanWrite(ClaimsPrincipal user, string providerName)
    {
        if (user.Identity?.IsAuthenticated != true) return false;

        var roles = _config.GetSection($"Dns:Providers:{providerName}:WriteRoles").Get<string[]>();
        if (roles is null || roles.Length == 0)
        {
            roles = _config.GetSection("Dns:DefaultWriteRoles").Get<string[]>() ?? ["DnsAdmins", "Domain Admins"];
        }

        return roles.Any(r => user.IsInRole(r));
    }

    public async Task<IReadOnlyList<DnsZone>> ListZonesAsync(string providerName = "Windows", CancellationToken ct = default)
    {
        var provider = GetProvider(providerName);
        var zones = await provider.GetZonesAsync(ct);

        if (_options.AllowedZones.Length > 0)
            zones = zones
                .Where(z => _options.AllowedZones.Contains(z.Name, StringComparer.OrdinalIgnoreCase))
                .ToList();

        return zones;
    }

    public Task<IReadOnlyList<DnsRecord>> ListRecordsAsync(
        string providerName, string zone, string? node = null, CancellationToken ct = default)
    {
        ValidateZone(zone);
        var provider = GetProvider(providerName);
        return provider.GetResourceRecordsAsync(zone, node, ct);
    }

    public Task AddRecordAsync(string providerName, AddRecordRequest request, CancellationToken ct = default)
    {
        ValidateZone(request.ZoneName);
        ValidateRecordType(request.RecordType);
        ValidateHostName(
            request.HostName,
            allowWildcard: request.RecordType == DnsRecordType.A,
            allowUnderscore: request.RecordType == DnsRecordType.TXT);
        ValidateRecordData(request.RecordType, request.Data);
        var provider = GetProvider(providerName);
        return provider.AddResourceRecordAsync(request, ct);
    }

    public Task DeleteRecordAsync(string providerName, DeleteRecordRequest request, CancellationToken ct = default)
    {
        ValidateZone(request.ZoneName);
        ValidateRecordType(request.RecordType);
        ValidateHostName(
            request.HostName,
            allowWildcard: request.RecordType == DnsRecordType.A,
            allowUnderscore: request.RecordType == DnsRecordType.TXT);
        var provider = GetProvider(providerName);
        return provider.RemoveResourceRecordAsync(request, ct);
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

            case DnsRecordType.CNAME: // Fallthrough
            case DnsRecordType.PTR:
                if (string.IsNullOrWhiteSpace(data))
                    throw new ArgumentException(
                        $"Record data cannot be empty for {type} records.");
                // The data for a CNAME or PTR must be a valid hostname.
                ValidateHostName(data, allowWildcard: false, allowUnderscore: false);
                break;

            case DnsRecordType.TXT:
                if (string.IsNullOrWhiteSpace(data))
                    throw new ArgumentException(
                        $"Record data cannot be empty for {type} records.");
                // Enforce a reasonable length limit to prevent abuse. 4096 is a generous limit
                // that accommodates long DKIM keys but prevents absurdly large inputs.
                if (data.Length > 4096)
                    throw new ArgumentException(
                        $"TXT record data is too long. Maximum length is 4096 characters.");
                break;
        }
    }
}
