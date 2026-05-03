namespace DnsGoBetween.Core.Models;

public class DnsZone
{
    public required string Name { get; init; }
    public string? ZoneType { get; init; }  // Primary, Secondary, Stub, Forwarder
    public bool IsDynamicUpdateEnabled { get; init; }
}
