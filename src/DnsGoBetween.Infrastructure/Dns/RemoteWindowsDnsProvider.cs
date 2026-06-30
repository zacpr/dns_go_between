using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Security;
using DnsGoBetween.Core.Interfaces;
using DnsGoBetween.Core.Models;
using Microsoft.Extensions.Configuration;

namespace DnsGoBetween.Infrastructure.Dns;

public sealed class RemoteWindowsDnsProvider : IDnsProvider, IDisposable
{
    private readonly string _server;
    private readonly PSCredential _credential;
    private readonly RunspacePool _runspacePool;
    private bool _disposed;

    public string ProviderName => "RemoteWindows";

    public RemoteWindowsDnsProvider(IConfiguration config)
    {
        _server = config.GetValue<string>("Dns:RemoteWindowsServer") ?? throw new ArgumentException("RemoteWindowsServer missing");
        var user = config.GetValue<string>("Dns:RemoteWindowsUser") ?? throw new ArgumentException("RemoteWindowsUser missing");
        var pass = config.GetValue<string>("Dns:RemoteWindowsPassword") ?? throw new ArgumentException("RemoteWindowsPassword missing");

        var securePass = new SecureString();
        foreach (var c in pass) securePass.AppendChar(c);
        securePass.MakeReadOnly();

        _credential = new PSCredential(user, securePass);

        var connectionInfo = new WSManConnectionInfo(new Uri($"http://{_server}:5985/wsman"), "http://schemas.microsoft.com/powershell/Microsoft.PowerShell", _credential);
        
        // Maintain a small pool of warm runspaces for fast concurrent requests
        _runspacePool = RunspaceFactory.CreateRunspacePool(1, 5, connectionInfo);
        _runspacePool.Open();
    }

    public async Task<IReadOnlyList<DnsZone>> GetZonesAsync(CancellationToken ct = default)
    {
        using var ps = PowerShell.Create();
        ps.RunspacePool = _runspacePool;
        ps.AddCommand("Get-DnsServerZone");

        var results = await InvokePowerShellAsync(ps, ct);
        
        return ParseZones(results);
    }

    internal static IReadOnlyList<DnsZone> ParseZones(IEnumerable<PSObject> results)
    {
        var zones = new List<DnsZone>();
        foreach (var pso in results)
        {
            var zoneName = GetStringProperty(pso, "ZoneName");
            if (string.IsNullOrWhiteSpace(zoneName)) continue;

            zones.Add(new DnsZone
            {
                Name = zoneName,
                ZoneType = GetStringProperty(pso, "ZoneType"),
                IsDynamicUpdateEnabled = GetStringProperty(pso, "DynamicUpdate") is "Secure" or "NonsecureAndSecure"
            });
        }
        return zones;
    }

    public async Task<IReadOnlyList<DnsRecord>> GetResourceRecordsAsync(string zone, string? node, CancellationToken ct = default)
    {
        using var ps = PowerShell.Create();
        ps.RunspacePool = _runspacePool;
        ps.AddCommand("Get-DnsServerResourceRecord")
          .AddParameter("ZoneName", zone);

        if (!string.IsNullOrWhiteSpace(node))
        {
            ps.AddParameter("Name", node);
        }

        var results = await InvokePowerShellAsync(ps, ct);
        return ParseRecords(zone, results);
    }

    internal static IReadOnlyList<DnsRecord> ParseRecords(string zone, IEnumerable<PSObject> results)
    {
        var records = new List<DnsRecord>();

        foreach (var pso in results)
        {
            var recordTypeStr = GetStringProperty(pso, "RecordType");
            if (!Enum.TryParse<DnsRecordType>(recordTypeStr, true, out var recordType))
                continue;

            var recordDataObj = GetNestedObject(pso, "RecordData");
            if (recordDataObj == null) continue;

            string data = string.Empty;
            switch (recordType)
            {
                case DnsRecordType.A:
                    data = GetStringProperty(recordDataObj, "IPv4Address");
                    break;
                case DnsRecordType.AAAA:
                    data = GetStringProperty(recordDataObj, "IPv6Address");
                    break;
                case DnsRecordType.CNAME:
                    data = GetStringProperty(recordDataObj, "HostNameAlias");
                    break;
                case DnsRecordType.PTR:
                    data = GetStringProperty(recordDataObj, "PtrDomainName");
                    break;
                case DnsRecordType.TXT:
                    data = GetStringArrayProperty(recordDataObj, "DescriptiveText");
                    break;
            }

            if (string.IsNullOrEmpty(data)) continue;

            records.Add(new DnsRecord
            {
                HostName = GetStringProperty(pso, "HostName"),
                ZoneName = zone,
                RecordType = recordType,
                Data = data,
                TimeToLive = GetTtlProperty(pso, "TimeToLive"),
                Timestamp = null
            });
        }
        return records;
    }

    public async Task AddResourceRecordAsync(AddRecordRequest request, CancellationToken ct = default)
    {
        using var ps = PowerShell.Create();
        ps.RunspacePool = _runspacePool;

        var ttl = TimeSpan.FromSeconds(request.TimeToLive);
        var cmd = request.RecordType switch
        {
            DnsRecordType.A => ps.AddCommand("Add-DnsServerResourceRecordA").AddParameter("IPv4Address", request.Data),
            DnsRecordType.AAAA => ps.AddCommand("Add-DnsServerResourceRecordAAAA").AddParameter("IPv6Address", request.Data),
            DnsRecordType.CNAME => ps.AddCommand("Add-DnsServerResourceRecordCName").AddParameter("HostNameAlias", request.Data),
            DnsRecordType.PTR => ps.AddCommand("Add-DnsServerResourceRecordPtr").AddParameter("PtrDomainName", request.Data),
            DnsRecordType.TXT => ps.AddCommand("Add-DnsServerResourceRecord").AddParameter("Txt", true).AddParameter("DescriptiveText", request.Data),
            _ => throw new NotSupportedException($"Record type {request.RecordType} is not supported.")
        };

        cmd.AddParameter("ZoneName", request.ZoneName)
           .AddParameter("Name", request.HostName)
           .AddParameter("TimeToLive", ttl);

        await InvokePowerShellAsync(ps, ct);
    }

    public async Task RemoveResourceRecordAsync(DeleteRecordRequest request, CancellationToken ct = default)
    {
        var records = await GetResourceRecordsAsync(request.ZoneName, request.HostName, ct);
        
        var target = records.FirstOrDefault(r => 
            r.RecordType == request.RecordType && 
            string.Equals(r.Data, request.Data, StringComparison.OrdinalIgnoreCase));

        if (target is null)
            throw new InvalidOperationException("No exact DNS record match found for deletion.");

        using var psRemove = PowerShell.Create();
        psRemove.RunspacePool = _runspacePool;
        psRemove.AddCommand("Remove-DnsServerResourceRecord")
                .AddParameter("ZoneName", request.ZoneName)
                .AddParameter("Name", request.HostName)
                .AddParameter("RRType", request.RecordType.ToString())
                .AddParameter("RecordData", request.Data)
                .AddParameter("Force", true);

        await InvokePowerShellAsync(psRemove, ct);
    }

    private static async Task<PSDataCollection<PSObject>> InvokePowerShellAsync(PowerShell ps, CancellationToken ct)
    {
        using var registration = ct.Register(() => ps.Stop());
        var task = Task.Factory.FromAsync(ps.BeginInvoke(), ps.EndInvoke);
        var results = await task.WaitAsync(ct);

        if (ps.HadErrors && ps.Streams.Error.Count > 0)
        {
            var error = ps.Streams.Error[0];
            throw new InvalidOperationException(error.Exception?.Message ?? error.ToString());
        }

        return results;
    }

    // ── Safe Property Extraction Helpers ─────────────────────────────────────

    internal static string GetStringProperty(PSObject? pso, string name)
    {
        return pso?.Properties[name]?.Value?.ToString() ?? string.Empty;
    }

    internal static string GetStringArrayProperty(PSObject? pso, string name)
    {
        var val = pso?.Properties[name]?.Value;
        if (val is string str) return str;
        if (val is System.Collections.IEnumerable enumerable)
        {
            var parts = new List<string>();
            foreach (var part in enumerable) parts.Add(part?.ToString() ?? "");
            return string.Join("", parts);
        }
        return string.Empty;
    }

    internal static PSObject? GetNestedObject(PSObject? pso, string name)
    {
        var val = pso?.Properties[name]?.Value;
        // AsPSObject safely wraps raw .NET objects and CIM instances into queryable PSObjects
        return val is null ? null : PSObject.AsPSObject(val);
    }

    internal static int GetTtlProperty(PSObject? pso, string name, int fallback = 3600)
    {
        var val = pso?.Properties[name]?.Value;
        if (val is TimeSpan ts) return (int)ts.TotalSeconds;
        if (val != null && TimeSpan.TryParse(val.ToString(), out var parsedTs)) return (int)parsedTs.TotalSeconds;
        
        return fallback;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _runspacePool?.Dispose();
            _disposed = true;
        }
    }
}