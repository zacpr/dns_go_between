using System.Collections.Concurrent;
using DnsGoBetween.Core.Configuration;
using Microsoft.Extensions.Options;

namespace DnsGoBetween.Api.Security;

public sealed class BasicAuthenticationAttemptLimiter
{
    private readonly ConcurrentDictionary<string, AttemptState> _attempts = new(StringComparer.Ordinal);
    private readonly AuthOptions _options;
    private readonly TimeProvider _timeProvider;

    public BasicAuthenticationAttemptLimiter(IOptions<AuthOptions> options)
        : this(options.Value, TimeProvider.System)
    {
    }

    public BasicAuthenticationAttemptLimiter(AuthOptions options, TimeProvider timeProvider)
    {
        _options = options;
        _timeProvider = timeProvider;
    }

    public bool IsLockedOut(string key, out TimeSpan retryAfter)
    {
        retryAfter = TimeSpan.Zero;

        if (!IsEnabled())
        {
            return false;
        }

        var now = _timeProvider.GetUtcNow();
        if (!_attempts.TryGetValue(key, out var state))
        {
            return false;
        }

        if (state.LockoutEndUtc is null)
        {
            return false;
        }

        if (state.LockoutEndUtc <= now)
        {
            _attempts.TryRemove(key, out _);
            return false;
        }

        retryAfter = state.LockoutEndUtc.Value - now;
        return true;
    }

    public void RegisterFailure(string key)
    {
        if (!IsEnabled())
        {
            return;
        }

        var now = _timeProvider.GetUtcNow();
        _attempts.AddOrUpdate(
            key,
            _ => CreateFailureState(now),
            (_, existing) => UpdateFailureState(existing, now));
    }

    public void RegisterSuccess(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        _attempts.TryRemove(key, out _);
    }

    private bool IsEnabled()
        => _options.BasicAuthenticationFailureLimit > 0 && _options.BasicAuthenticationLockoutSeconds > 0;

    private AttemptState CreateFailureState(DateTimeOffset now)
    {
        var failureCount = 1;
        DateTimeOffset? lockoutEndUtc = null;
        if (failureCount >= _options.BasicAuthenticationFailureLimit)
        {
            lockoutEndUtc = now.AddSeconds(_options.BasicAuthenticationLockoutSeconds);
        }

        return new AttemptState(failureCount, lockoutEndUtc);
    }

    private AttemptState UpdateFailureState(AttemptState existing, DateTimeOffset now)
    {
        if (existing.LockoutEndUtc is not null && existing.LockoutEndUtc > now)
        {
            return existing;
        }

        var failureCount = existing.LockoutEndUtc is not null && existing.LockoutEndUtc <= now
            ? 1
            : existing.FailureCount + 1;

        DateTimeOffset? lockoutEndUtc = null;
        if (failureCount >= _options.BasicAuthenticationFailureLimit)
        {
            lockoutEndUtc = now.AddSeconds(_options.BasicAuthenticationLockoutSeconds);
        }

        return new AttemptState(failureCount, lockoutEndUtc);
    }

    private sealed record AttemptState(int FailureCount, DateTimeOffset? LockoutEndUtc);
}