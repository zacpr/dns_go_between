using DnsGoBetween.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace DnsGoBetween.Infrastructure.Audit;

public sealed class StructuredAuditLogger : IAuditLogger
{
    private readonly ILogger<StructuredAuditLogger> _logger;
    private readonly IAuditHistoryStore _historyStore;

    public StructuredAuditLogger(ILogger<StructuredAuditLogger> logger, IAuditHistoryStore historyStore)
    {
        _logger = logger;
        _historyStore = historyStore;
    }

    public void LogWrite(
        string user,
        string action,
        string target,
        bool success,
        string? errorMessage = null,
        string? correlationId = null)
    {
        var cid = correlationId ?? Guid.NewGuid().ToString("N");
        var normalizedTarget = string.IsNullOrWhiteSpace(target) ? "unspecified" : target;
        var normalizedError = string.IsNullOrWhiteSpace(errorMessage) ? "None" : errorMessage;

        if (success)
            _logger.LogInformation(
                "[AUDIT] CorrelationId={CorrelationId} User={User} Action={Action} Target={Target} Outcome=Success",
                cid, user, action, normalizedTarget);
        else
            _logger.LogWarning(
                "[AUDIT] CorrelationId={CorrelationId} User={User} Action={Action} Target={Target} Outcome=Failure FailureType={FailureType}",
                cid, user, action, normalizedTarget, normalizedError);

        _historyStore.AddEntry(new AuditHistoryEntry
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            User = user,
            Action = action,
            Target = normalizedTarget,
            Success = success,
            ErrorMessage = success ? null : normalizedError,
            CorrelationId = cid
        });
    }
}
