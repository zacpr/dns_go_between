using DnsGoBetween.Core.Configuration;
using DnsGoBetween.Core.Interfaces;
using DnsGoBetween.Core.Models;
using DnsGoBetween.Infrastructure.Dns;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace DnsGoBetween.Tests;

public class DnsRecordValidationTests
{
    private static DnsRecordService CreateService(
        IPowerShellDnsExecutor executor,
        string[]? allowedZones = null,
        string[]? allowedTypes = null)
    {
        var opts = Options.Create(new DnsOptions
        {
            AllowedZones = allowedZones ?? [],
            AllowedRecordTypes = allowedTypes ?? ["A", "AAAA", "CNAME", "PTR"]
        });
        return new DnsRecordService(executor, opts, NullLogger<DnsRecordService>.Instance);
    }

    // ── Zone allowlist ────────────────────────────────────────────────────────

    [Fact]
    public async Task AddRecord_BlockedZone_ThrowsUnauthorizedAccessException()
    {
        var executor = new Mock<IPowerShellDnsExecutor>();
        var svc = CreateService(executor.Object, allowedZones: ["allowed.local"]);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            svc.AddRecordAsync(new AddRecordRequest
            {
                ZoneName = "evil.zone",
                HostName = "test",
                RecordType = DnsRecordType.A,
                Data = "1.2.3.4"
            }));
    }

    [Fact]
    public async Task ListZones_FiltersToAllowedZones()
    {
        var executor = new Mock<IPowerShellDnsExecutor>();
        executor.Setup(e => e.GetZonesAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<DnsZone>
                {
                    new() { Name = "allowed.local" },
                    new() { Name = "forbidden.zone" },
                    new() { Name = "also.allowed.local" }
                });

        var svc = CreateService(executor.Object, allowedZones: ["allowed.local", "also.allowed.local"]);
        var zones = await svc.ListZonesAsync();

        Assert.Equal(2, zones.Count);
        Assert.DoesNotContain(zones, z => z.Name == "forbidden.zone");
    }

    // ── Record type allowlist ─────────────────────────────────────────────────

    [Fact]
    public async Task AddRecord_DisallowedRecordType_ThrowsNotSupportedException()
    {
        var executor = new Mock<IPowerShellDnsExecutor>();
        var svc = CreateService(executor.Object, allowedTypes: ["A"]);

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            svc.AddRecordAsync(new AddRecordRequest
            {
                ZoneName = "example.local",
                HostName = "test",
                RecordType = DnsRecordType.MX,
                Data = "mail.example.local"
            }));
    }

    // ── IPv4 validation ───────────────────────────────────────────────────────

    [Fact]
    public async Task AddRecord_InvalidIPv4_ThrowsArgumentException()
    {
        var executor = new Mock<IPowerShellDnsExecutor>();
        var svc = CreateService(executor.Object);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.AddRecordAsync(new AddRecordRequest
            {
                ZoneName = "example.local",
                HostName = "test",
                RecordType = DnsRecordType.A,
                Data = "not-an-ip"
            }));
    }

    [Fact]
    public async Task AddRecord_IPv6AsA_ThrowsArgumentException()
    {
        var executor = new Mock<IPowerShellDnsExecutor>();
        var svc = CreateService(executor.Object);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.AddRecordAsync(new AddRecordRequest
            {
                ZoneName = "example.local",
                HostName = "test",
                RecordType = DnsRecordType.A,
                Data = "::1"
            }));
    }

    // ── IPv6 validation ───────────────────────────────────────────────────────

    [Fact]
    public async Task AddRecord_InvalidIPv6_ThrowsArgumentException()
    {
        var executor = new Mock<IPowerShellDnsExecutor>();
        var svc = CreateService(executor.Object);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.AddRecordAsync(new AddRecordRequest
            {
                ZoneName = "example.local",
                HostName = "test",
                RecordType = DnsRecordType.AAAA,
                Data = "not-ipv6"
            }));
    }

    // ── Hostname validation ───────────────────────────────────────────────────

    [Theory]
    [InlineData("valid-host")]
    [InlineData("host.sub.domain")]
    [InlineData("@")]
    [InlineData("*")]
    [InlineData("host123")]
    [InlineData("a")]
    public async Task AddRecord_ValidHostName_DoesNotThrow(string hostName)
    {
        var executor = new Mock<IPowerShellDnsExecutor>();
        executor
            .Setup(e => e.AddResourceRecordAsync(It.IsAny<AddRecordRequest>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var svc = CreateService(executor.Object);
        var ex = await Record.ExceptionAsync(() =>
            svc.AddRecordAsync(new AddRecordRequest
            {
                ZoneName = "example.local",
                HostName = hostName,
                RecordType = DnsRecordType.A,
                Data = "192.168.1.1"
            }));

        Assert.Null(ex);
    }

    [Fact]
    public async Task AddRecord_WildcardCName_ThrowsArgumentException()
    {
        var executor = new Mock<IPowerShellDnsExecutor>();
        var svc = CreateService(executor.Object);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.AddRecordAsync(new AddRecordRequest
            {
                ZoneName = "example.local",
                HostName = "*",
                RecordType = DnsRecordType.CNAME,
                Data = "target.example.local"
            }));
    }

    [Theory]
    [InlineData("-invalid")]
    [InlineData("invalid-")]
    [InlineData("inval!d")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task AddRecord_InvalidHostName_ThrowsArgumentException(string hostName)
    {
        var executor = new Mock<IPowerShellDnsExecutor>();
        var svc = CreateService(executor.Object);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.AddRecordAsync(new AddRecordRequest
            {
                ZoneName = "example.local",
                HostName = hostName,
                RecordType = DnsRecordType.A,
                Data = "1.2.3.4"
            }));
    }

    // ── CNAME / PTR data ──────────────────────────────────────────────────────

    [Fact]
    public async Task AddRecord_CnameEmptyData_ThrowsArgumentException()
    {
        var executor = new Mock<IPowerShellDnsExecutor>();
        var svc = CreateService(executor.Object);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.AddRecordAsync(new AddRecordRequest
            {
                ZoneName = "example.local",
                HostName = "alias",
                RecordType = DnsRecordType.CNAME,
                Data = ""
            }));
    }

    // ── Delete zone check ─────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteRecord_BlockedZone_ThrowsUnauthorizedAccessException()
    {
        var executor = new Mock<IPowerShellDnsExecutor>();
        var svc = CreateService(executor.Object, allowedZones: ["allowed.local"]);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            svc.DeleteRecordAsync(new DeleteRecordRequest
            {
                ZoneName = "evil.zone",
                HostName = "test",
                RecordType = DnsRecordType.A,
                Data = "1.2.3.4"
            }));
    }
}
