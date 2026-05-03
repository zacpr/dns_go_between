namespace DnsGoBetween.Core.Models;

public class DeleteRecordRequest
{
    public required string ZoneName { get; init; }
    public required string HostName { get; init; }
    public required DnsRecordType RecordType { get; init; }
    public required string Data { get; init; }  // Must match exactly for safe delete semantics
}
