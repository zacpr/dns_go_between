using DnsGoBetween.Api.Security;
using DnsGoBetween.Core.Configuration;
using System.Runtime.Versioning;
using Xunit;

namespace DnsGoBetween.Tests;

[SupportedOSPlatform("windows")]
public class BasicAuthenticationAttemptLimiterTests
{
    [Fact]
    public void RegisterFailure_LocksOutAfterConfiguredThreshold()
    {
        var timeProvider = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var limiter = new BasicAuthenticationAttemptLimiter(
            new AuthOptions
            {
                BasicAuthenticationFailureLimit = 3,
                BasicAuthenticationLockoutSeconds = 120
            },
            timeProvider);

        limiter.RegisterFailure("10.0.0.1");
        limiter.RegisterFailure("10.0.0.1");

        Assert.False(limiter.IsLockedOut("10.0.0.1", out _));

        limiter.RegisterFailure("10.0.0.1");

        Assert.True(limiter.IsLockedOut("10.0.0.1", out var retryAfter));
        Assert.True(retryAfter > TimeSpan.Zero);
    }

    [Fact]
    public void RegisterSuccess_ClearsExistingLockoutState()
    {
        var timeProvider = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var limiter = new BasicAuthenticationAttemptLimiter(
            new AuthOptions
            {
                BasicAuthenticationFailureLimit = 2,
                BasicAuthenticationLockoutSeconds = 120
            },
            timeProvider);

        limiter.RegisterFailure("10.0.0.2");
        limiter.RegisterFailure("10.0.0.2");

        Assert.True(limiter.IsLockedOut("10.0.0.2", out _));

        limiter.RegisterSuccess("10.0.0.2");

        Assert.False(limiter.IsLockedOut("10.0.0.2", out _));
    }

    [Fact]
    public void ExpiredLockout_IsRemovedOnNextCheck()
    {
        var timeProvider = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var limiter = new BasicAuthenticationAttemptLimiter(
            new AuthOptions
            {
                BasicAuthenticationFailureLimit = 1,
                BasicAuthenticationLockoutSeconds = 60
            },
            timeProvider);

        limiter.RegisterFailure("10.0.0.3");

        Assert.True(limiter.IsLockedOut("10.0.0.3", out _));

        timeProvider.Advance(TimeSpan.FromSeconds(61));

        Assert.False(limiter.IsLockedOut("10.0.0.3", out _));
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow;

        public FakeTimeProvider(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan duration)
        {
            _utcNow = _utcNow.Add(duration);
        }
    }
}