using System.Text.Json;
using DnsGoBetween.Core.Interfaces;
using Microsoft.Extensions.Configuration;

namespace DnsGoBetween.Infrastructure.Audit;

public sealed class FileAuditHistoryStore : IAuditHistoryStore
{
    private readonly string _filePath;
    private readonly object _gate = new();
    private readonly int _retentionDays;
    private List<AuditHistoryEntry> _cache = new();
    private bool _loaded;

    public FileAuditHistoryStore(IConfiguration config)
    {
        _filePath = Path.Combine(AppContext.BaseDirectory, "audit_history.json");
        // Configurable retention. Defaults to 30 days.
        _retentionDays = config.GetValue<int>("Audit:HistoryRetentionDays", 30);
    }

    public void AddEntry(AuditHistoryEntry entry)
    {
        lock (_gate)
        {
            EnsureLoaded();
            _cache.Add(entry);
            
            var cutoff = DateTimeOffset.UtcNow.AddDays(-_retentionDays);
            _cache.RemoveAll(e => e.TimestampUtc < cutoff);
            
            File.WriteAllText(_filePath, JsonSerializer.Serialize(_cache));
        }
    }

    public IReadOnlyList<AuditHistoryEntry> GetEntries()
    {
        lock (_gate)
        {
            EnsureLoaded();
            return _cache.ToList();
        }
    }

    private void EnsureLoaded()
    {
        if (_loaded) return;
        if (File.Exists(_filePath))
        {
            var json = File.ReadAllText(_filePath);
            _cache = JsonSerializer.Deserialize<List<AuditHistoryEntry>>(json) ?? new();
        }
        _loaded = true;
    }
}