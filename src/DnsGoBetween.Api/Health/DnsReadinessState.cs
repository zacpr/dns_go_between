namespace DnsGoBetween.Api.Health;

public sealed class DnsReadinessState
{
    private readonly object _gate = new();
    private DnsReadinessSnapshot _snapshot = DnsReadinessSnapshot.Uninitialized;

    public DnsReadinessSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            return _snapshot;
        }
    }

    public void ReportSuccess(DateTimeOffset nowUtc)
    {
        lock (_gate)
        {
            _snapshot = new DnsReadinessSnapshot(
                IsReady: true,
                LastCheckUtc: nowUtc,
                LastSuccessUtc: nowUtc,
                LastError: null);
        }
    }

    public void ReportFailure(DateTimeOffset nowUtc, string error)
    {
        lock (_gate)
        {
            _snapshot = new DnsReadinessSnapshot(
                IsReady: false,
                LastCheckUtc: nowUtc,
                LastSuccessUtc: _snapshot.LastSuccessUtc,
                LastError: error);
        }
    }
}

public sealed record DnsReadinessSnapshot(
    bool IsReady,
    DateTimeOffset? LastCheckUtc,
    DateTimeOffset? LastSuccessUtc,
    string? LastError)
{
    public static DnsReadinessSnapshot Uninitialized { get; } =
        new(false, null, null, "Readiness probe has not completed yet.");
}