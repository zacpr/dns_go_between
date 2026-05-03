namespace DnsGoBetween.Core.Models;

public class DnsRecord
{
    public required string HostName { get; init; }
    public required string ZoneName { get; init; }
    public required DnsRecordType RecordType { get; init; }
    public required string Data { get; init; }  // IPv4/6 for A/AAAA, FQDN for CNAME/PTR, text for TXT
    public int TimeToLive { get; init; }
    public DateTime? Timestamp { get; init; }
}
