using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using DnsGoBetween.Core.Configuration;
using DnsGoBetween.Core.Interfaces;
using DnsGoBetween.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DnsGoBetween.Infrastructure.Dns;

/// <summary>
/// Executes DNS Server PowerShell cmdlets directly on the local machine.
/// Requires the DnsServer role and RSAT DNS tools to be installed.
/// </summary>
public sealed class LocalPowerShellDnsExecutor : IPowerShellDnsExecutor
{
    private readonly ILogger<LocalPowerShellDnsExecutor> _logger;
    private readonly DnsOptions _options;
    private const string WindowsPowerShellPath = @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe";

    public LocalPowerShellDnsExecutor(
        ILogger<LocalPowerShellDnsExecutor> logger,
        IOptions<DnsOptions> options)
    {
        _logger = logger;
        _options = options.Value;
    }

    public async Task<IReadOnlyList<DnsZone>> GetZonesAsync(CancellationToken ct = default)
    {
        var script = @"
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
Import-Module DnsServer -ErrorAction Stop
Get-DnsServerZone |
    ForEach-Object {
        [pscustomobject]@{
            Name = $_.ZoneName
            ZoneType = $_.ZoneType
            IsDynamicUpdateEnabled = ($_.DynamicUpdate -in @('Secure','NonsecureAndSecure'))
        }
    } |
    ConvertTo-Json -Compress
";

        var output = await InvokeWindowsPowerShellAsync(script, ct);
        return DeserializeMany(output, element => new DnsZone
        {
            Name = element.GetProperty("Name").GetString() ?? string.Empty,
            ZoneType = element.TryGetProperty("ZoneType", out var zoneType) ? zoneType.GetString() : null,
            IsDynamicUpdateEnabled = element.TryGetProperty("IsDynamicUpdateEnabled", out var dynamicUpdate) && dynamicUpdate.ValueKind is JsonValueKind.True
        })
        .Where(z => !string.IsNullOrWhiteSpace(z.Name))
        .ToList();
    }

    public async Task<IReadOnlyList<DnsRecord>> GetResourceRecordsAsync(
        string zone, string? node, CancellationToken ct = default)
    {
        var nodeFilter = string.IsNullOrWhiteSpace(node)
            ? string.Empty
            : $" | Where-Object {{ $_.HostName -eq {QuotePs(node)} }}";

        var script = $@"
    $ErrorActionPreference = 'Stop'
    $ProgressPreference = 'SilentlyContinue'
Import-Module DnsServer -ErrorAction Stop
Get-DnsServerResourceRecord -ZoneName {QuotePs(zone)}{nodeFilter} |
    ForEach-Object {{
        $recordType = $_.RecordType.ToString()
        if ($recordType -eq 'A' -and $null -ne $_.RecordData -and $null -ne $_.RecordData.IPv4Address) {{
            [pscustomobject]@{{
                HostName = $_.HostName
                ZoneName = {QuotePs(zone)}
                RecordType = $recordType
                Data = $_.RecordData.IPv4Address.ToString()
                TimeToLive = [int]$_.TimeToLive.TotalSeconds
                Timestamp = $_.Timestamp
            }}
        }}
        elseif ($recordType -eq 'AAAA' -and $null -ne $_.RecordData -and $null -ne $_.RecordData.IPv6Address) {{
            [pscustomobject]@{{
                HostName = $_.HostName
                ZoneName = {QuotePs(zone)}
                RecordType = $recordType
                Data = $_.RecordData.IPv6Address.ToString()
                TimeToLive = [int]$_.TimeToLive.TotalSeconds
                Timestamp = $_.Timestamp
            }}
        }}
        elseif ($recordType -eq 'CNAME' -and $null -ne $_.RecordData -and $null -ne $_.RecordData.HostNameAlias) {{
            [pscustomobject]@{{
                HostName = $_.HostName
                ZoneName = {QuotePs(zone)}
                RecordType = $recordType
                Data = $_.RecordData.HostNameAlias.ToString()
                TimeToLive = [int]$_.TimeToLive.TotalSeconds
                Timestamp = $_.Timestamp
            }}
        }}
        elseif ($recordType -eq 'PTR' -and $null -ne $_.RecordData -and $null -ne $_.RecordData.PtrDomainName) {{
            [pscustomobject]@{{
                HostName = $_.HostName
                ZoneName = {QuotePs(zone)}
                RecordType = $recordType
                Data = $_.RecordData.PtrDomainName.ToString()
                TimeToLive = [int]$_.TimeToLive.TotalSeconds
                Timestamp = $_.Timestamp
            }}
        }}
        elseif ($recordType -eq 'TXT' -and $null -ne $_.RecordData -and $null -ne $_.RecordData.DescriptiveText) {{
            [pscustomobject]@{{
                HostName = $_.HostName
                ZoneName = {QuotePs(zone)}
                RecordType = $recordType
                Data = (@($_.RecordData.DescriptiveText) -join '')
                TimeToLive = [int]$_.TimeToLive.TotalSeconds
                Timestamp = $_.Timestamp
            }}
        }}
    }} |
    ConvertTo-Json -Compress
";

        var output = await InvokeWindowsPowerShellAsync(script, ct);
        return DeserializeMany(output, element => new DnsRecord
        {
            HostName = element.GetProperty("HostName").GetString() ?? string.Empty,
            ZoneName = element.GetProperty("ZoneName").GetString() ?? zone,
            RecordType = Enum.Parse<DnsRecordType>(element.GetProperty("RecordType").GetString() ?? string.Empty, true),
            Data = element.GetProperty("Data").GetString() ?? string.Empty,
            TimeToLive = element.TryGetProperty("TimeToLive", out var ttl) ? ttl.GetInt32() : 3600,
            Timestamp = element.TryGetProperty("Timestamp", out var timestamp) && timestamp.ValueKind != JsonValueKind.Null
                ? TryParseTimestamp(timestamp)
                : null
        });
    }

    public async Task AddResourceRecordAsync(AddRecordRequest request, CancellationToken ct = default)
    {
        if (request.RecordType == DnsRecordType.A && request.HostName == "*")
        {
            await AddWildcardARecordWithDnsCmdAsync(request, ct);
            return;
        }

        var ttl = TimeSpan.FromSeconds(request.TimeToLive);
        var ttlPs = QuotePs(ttl.ToString("c"));
        var script = request.RecordType switch
        {
            DnsRecordType.A => $@"
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
Import-Module DnsServer -ErrorAction Stop
Add-DnsServerResourceRecordA -ZoneName {QuotePs(request.ZoneName)} -Name {QuotePs(request.HostName)} -IPv4Address {QuotePs(request.Data)} -TimeToLive ([TimeSpan]{ttlPs})
",
            DnsRecordType.AAAA => $@"
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
Import-Module DnsServer -ErrorAction Stop
Add-DnsServerResourceRecordAAAA -ZoneName {QuotePs(request.ZoneName)} -Name {QuotePs(request.HostName)} -IPv6Address {QuotePs(request.Data)} -TimeToLive ([TimeSpan]{ttlPs})
",
            DnsRecordType.CNAME => $@"
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
Import-Module DnsServer -ErrorAction Stop
Add-DnsServerResourceRecordCName -ZoneName {QuotePs(request.ZoneName)} -Name {QuotePs(request.HostName)} -HostNameAlias {QuotePs(request.Data)} -TimeToLive ([TimeSpan]{ttlPs})
",
            DnsRecordType.PTR => $@"
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
Import-Module DnsServer -ErrorAction Stop
Add-DnsServerResourceRecordPtr -ZoneName {QuotePs(request.ZoneName)} -Name {QuotePs(request.HostName)} -PtrDomainName {QuotePs(request.Data)} -TimeToLive ([TimeSpan]{ttlPs})
",
            DnsRecordType.TXT => $@"
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
Import-Module DnsServer -ErrorAction Stop
Add-DnsServerResourceRecord -ZoneName {QuotePs(request.ZoneName)} -Name {QuotePs(request.HostName)} -Txt -DescriptiveText {QuotePs(request.Data)} -TimeToLive ([TimeSpan]{ttlPs})
",
            _ => throw new NotSupportedException($"Record type {request.RecordType} is not supported in v1.")
        };

        await InvokeWindowsPowerShellAsync(script, ct);
    }

    private async Task AddWildcardARecordWithDnsCmdAsync(AddRecordRequest request, CancellationToken ct)
    {
        if (request.TimeToLive != 3600)
        {
            _logger.LogWarning(
                "Wildcard A record for zone {ZoneName} requested TTL {RequestedTtl}. dnscmd fallback will use the server or zone default TTL.",
                request.ZoneName,
                request.TimeToLive);
        }

        var script = $@"
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$output = & dnscmd.exe /recordadd {QuotePs(request.ZoneName)} '*' A {QuotePs(request.Data)} 2>&1
if ($LASTEXITCODE -ne 0) {{
    throw ((@($output) -join [Environment]::NewLine).Trim())
}}
";

        await InvokeWindowsPowerShellAsync(script, ct);
    }

    public async Task RemoveResourceRecordAsync(DeleteRecordRequest request, CancellationToken ct = default)
    {
        var recordType = request.RecordType.ToString();
        var script = $@"
    $ErrorActionPreference = 'Stop'
    $ProgressPreference = 'SilentlyContinue'
Import-Module DnsServer -ErrorAction Stop

$target = Get-DnsServerResourceRecord -ZoneName {QuotePs(request.ZoneName)} -Name {QuotePs(request.HostName)} -RRType {QuotePs(recordType)} |
    Where-Object {{
        if ($null -eq $_.RecordData) {{
            return $false
        }}

        $data = switch ({QuotePs(recordType)}) {{
            'A' {{ if ($null -ne $_.RecordData.IPv4Address) {{ $_.RecordData.IPv4Address.ToString() }} else {{ $null }} }}
            'AAAA' {{ if ($null -ne $_.RecordData.IPv6Address) {{ $_.RecordData.IPv6Address.ToString() }} else {{ $null }} }}
            'CNAME' {{ if ($null -ne $_.RecordData.HostNameAlias) {{ $_.RecordData.HostNameAlias.ToString() }} else {{ $null }} }}
            'PTR' {{ if ($null -ne $_.RecordData.PtrDomainName) {{ $_.RecordData.PtrDomainName.ToString() }} else {{ $null }} }}
            'TXT' {{ if ($null -ne $_.RecordData.DescriptiveText) {{ (@($_.RecordData.DescriptiveText) -join '') }} else {{ $null }} }}
            Default {{ $null }}
        }}

        $data -eq {QuotePs(request.Data)}
    }} |
    Select-Object -First 1

if ($null -eq $target) {{
    throw 'No exact DNS record match found for deletion.'
}}

Remove-DnsServerResourceRecord -ZoneName {QuotePs(request.ZoneName)} -InputObject $target -Force
";

        await InvokeWindowsPowerShellAsync(script, ct);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private async Task<string> InvokeWindowsPowerShellAsync(string script, CancellationToken externalCt)
        {
        using var timeoutCts = new CancellationTokenSource(
            TimeSpan.FromSeconds(_options.CommandTimeoutSeconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(externalCt, timeoutCts.Token);

        if (!File.Exists(WindowsPowerShellPath))
            throw new FileNotFoundException("Windows PowerShell was not found.", WindowsPowerShellPath);

        var encodedCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var startInfo = new ProcessStartInfo
        {
            FileName = WindowsPowerShellPath,
            Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encodedCommand}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = startInfo };

        process.Start();

        var stdOutTask = process.StandardOutput.ReadToEndAsync();
        var stdErrTask = process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync(linked.Token);

        var stdOut = await stdOutTask;
        var stdErr = await stdErrTask;

        try
        {
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"DNS cmdlet error: {stdErr.Trim()}".Trim());
            }

            return stdOut.Trim();
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(true);
            }
            catch
            {
            }

            throw new TimeoutException(
                $"DNS cmdlet timed out after {_options.CommandTimeoutSeconds}s.");
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(true);
            }
            catch
            {
            }

            throw;
        }
    }

    private static IReadOnlyList<T> DeserializeMany<T>(string json, Func<JsonElement, T> map)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Array.Empty<T>();

        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.ValueKind switch
        {
            JsonValueKind.Array => doc.RootElement.EnumerateArray().Select(map).ToList(),
            JsonValueKind.Object => new[] { map(doc.RootElement) },
            _ => Array.Empty<T>()
        };
    }

    private static string QuotePs(string value)
    {
        return $"'{value.Replace("'", "''")}'";
    }

    private static DateTime? TryParseTimestamp(JsonElement timestamp)
    {
        if (timestamp.ValueKind == JsonValueKind.String)
        {
            var value = timestamp.GetString();
            if (string.IsNullOrWhiteSpace(value))
                return null;

            if (DateTime.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal | DateTimeStyles.AllowWhiteSpaces,
                out var invariantParsed))
            {
                return invariantParsed;
            }

            if (DateTime.TryParse(
                value,
                CultureInfo.CurrentCulture,
                DateTimeStyles.AssumeLocal | DateTimeStyles.AllowWhiteSpaces,
                out var currentParsed))
            {
                return currentParsed;
            }

            return null;
        }

        if (timestamp.ValueKind == JsonValueKind.Number && timestamp.TryGetInt64(out var unixSeconds))
        {
            try
            {
                return DateTimeOffset.FromUnixTimeSeconds(unixSeconds).LocalDateTime;
            }
            catch
            {
                return null;
            }
        }

        return null;
    }
}
