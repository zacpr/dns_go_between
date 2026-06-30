namespace DnsGoBetween.Core.Models;

public class DnsRecord
{
    public required string HostName { get; init; }
    public required string ZoneName { get; init; }
    public required DnsRecordType RecordType { get; init; }
    public required string Data { get; init; }  // IPv4/6 for A/AAAA, FQDN for CNAME/PTR, text for TXT
    public int TimeToLive { get; init; }
    public DateTime? Timestamp { get; init; }

    /// <summary>
    /// Hostname normalized for display: the zone suffix is stripped if the
    /// record happens to be stored with its fully-qualified name (e.g. record
    /// <c>all.example.com</c> stored inside zone <c>example.com</c> displays as
    /// <c>all</c>). The apex shows as <c>@</c>. Use this for grouping and tree
    /// rendering; always use <see cref="HostName"/> for delete operations so the
    /// exact stored entry is targeted.
    /// </summary>
    public string DisplayName
    {
        get
        {
            if (string.IsNullOrEmpty(HostName) || string.IsNullOrEmpty(ZoneName))
                return HostName;
            if (string.Equals(HostName, ZoneName, StringComparison.OrdinalIgnoreCase))
                return "@";
            var suffix = "." + ZoneName;
            if (HostName.Length > suffix.Length &&
                HostName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return HostName[..^suffix.Length];
            }
            return HostName;
        }
    }

    /// <summary>
    /// True when the stored <see cref="HostName"/> contains the zone suffix
    /// (or equals the zone). Such records actually answer queries for
    /// <c>HostName.ZoneName</c>, which is almost always unintended — callers
    /// should surface this so operators can clean up duplicates.
    /// </summary>
    public bool HasFqdnHostName =>
        !string.Equals(HostName, DisplayName, StringComparison.Ordinal);
}
