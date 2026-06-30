namespace DnsGoBetween.Core.Interfaces;

public interface IAuditHistoryStore
{
    void AddEntry(AuditHistoryEntry entry);
    IReadOnlyList<AuditHistoryEntry> GetEntries();
}

public class AuditHistoryEntry
{
    public DateTimeOffset TimestampUtc { get; init; }
    public string User { get; init; } = "";
    public string Action { get; init; } = "";
    public string Target { get; init; } = "";
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public string? CorrelationId { get; init; }
}