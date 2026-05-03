namespace DnsGoBetween.Core.Interfaces;

public interface IAuditLogger
{
    void LogWrite(
        string user,
        string action,
        string target,
        bool success,
        string? errorMessage = null,
        string? correlationId = null);
}
