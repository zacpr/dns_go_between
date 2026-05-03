namespace DnsGoBetween.Core.Models;

public class AddRecordRequest
{
    public required string ZoneName { get; init; }
    public required string HostName { get; init; }
    public required DnsRecordType RecordType { get; init; }
    public required string Data { get; init; }
    public int TimeToLive { get; init; } = 3600;
}
