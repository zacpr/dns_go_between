namespace DnsGoBetween.Core.Configuration;

public class DnsOptions
{
    public const string SectionName = "Dns";

    /// <summary>When non-empty, list/add/delete are restricted to these zones only.</summary>
    public string[] AllowedZones { get; set; } = [];

    /// <summary>Record types permitted for write operations.</summary>
    public string[] AllowedRecordTypes { get; set; } = ["A", "AAAA", "CNAME", "PTR"];

    /// <summary>Seconds before a PowerShell cmdlet call is cancelled.</summary>
    public int CommandTimeoutSeconds { get; set; } = 30;

    // v2 toggle — when true the RemotePowerShellDnsExecutor is used instead
    public bool UseRemoteProvider { get; set; } = false;
    public string? RemoteComputerName { get; set; }
}
