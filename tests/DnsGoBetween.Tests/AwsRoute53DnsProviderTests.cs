using Amazon.Route53;
using Amazon.Route53.Model;
using DnsGoBetween.Core.Models;
using DnsGoBetween.Infrastructure.Dns;
using FluentAssertions;
using Moq;
using Xunit;

namespace DnsGoBetween.Tests;

public class AwsRoute53DnsProviderTests
{
    private const string ZoneName = "example.com";
    private const string ZoneId = "/hostedzone/Z123ABC";

    private static (AwsRoute53DnsProvider Sut, Mock<IAmazonRoute53> Client) CreateSut()
    {
        var client = new Mock<IAmazonRoute53>(MockBehavior.Strict);
        var sut = new AwsRoute53DnsProvider(client.Object);
        return (sut, client);
    }

    private static void StubZoneLookup(Mock<IAmazonRoute53> client)
    {
        client.Setup(c => c.ListHostedZonesByNameAsync(
                It.Is<ListHostedZonesByNameRequest>(r => r.DNSName == ZoneName),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListHostedZonesByNameResponse
            {
                HostedZones = new List<HostedZone>
                {
                    new() { Id = ZoneId, Name = $"{ZoneName}." }
                }
            });
    }

    // ── GetZonesAsync ─────────────────────────────────────────────────────────

    [Fact]
    public async Task GetZonesAsync_TrimsTrailingDotAndReturnsZones()
    {
        var (sut, client) = CreateSut();
        client.Setup(c => c.ListHostedZonesAsync(It.IsAny<ListHostedZonesRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListHostedZonesResponse
            {
                HostedZones = new List<HostedZone>
                {
                    new() { Id = "/hostedzone/Z1", Name = "example.com." },
                    new() { Id = "/hostedzone/Z2", Name = "another.net." }
                }
            });

        var zones = await sut.GetZonesAsync();

        zones.Should().HaveCount(2);
        zones[0].Name.Should().Be("example.com");
        zones[0].ZoneType.Should().Be("Primary");
        zones[0].IsDynamicUpdateEnabled.Should().BeTrue();
        zones[1].Name.Should().Be("another.net");
    }

    // ── GetResourceRecordsAsync ───────────────────────────────────────────────

    [Fact]
    public async Task GetResourceRecordsAsync_ParsesRecordsAndUnquotesTxt()
    {
        var (sut, client) = CreateSut();
        StubZoneLookup(client);

        client.Setup(c => c.ListResourceRecordSetsAsync(
                It.IsAny<ListResourceRecordSetsRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListResourceRecordSetsResponse
            {
                ResourceRecordSets = new List<ResourceRecordSet>
                {
                    new()
                    {
                        Name = "www.example.com.",
                        Type = "A",
                        TTL = 300,
                        ResourceRecords = new List<ResourceRecord> { new() { Value = "1.2.3.4" } }
                    },
                    new()
                    {
                        Name = "_acme-challenge.example.com.",
                        Type = "TXT",
                        TTL = 60,
                        ResourceRecords = new List<ResourceRecord> { new() { Value = "\"verification-token\"" } }
                    }
                }
            });

        var records = await sut.GetResourceRecordsAsync(ZoneName, node: null);

        records.Should().HaveCount(2);
        records[0].HostName.Should().Be("www");
        records[0].RecordType.Should().Be(DnsRecordType.A);
        records[0].Data.Should().Be("1.2.3.4");
        records[0].TimeToLive.Should().Be(300);
        records[0].ZoneName.Should().Be(ZoneName);

        records[1].HostName.Should().Be("_acme-challenge");
        records[1].RecordType.Should().Be(DnsRecordType.TXT);
        records[1].Data.Should().Be("verification-token");  // surrounding quotes stripped
    }

    [Fact]
    public async Task GetResourceRecordsAsync_FiltersUnsupportedRecordTypes()
    {
        var (sut, client) = CreateSut();
        StubZoneLookup(client);

        client.Setup(c => c.ListResourceRecordSetsAsync(
                It.IsAny<ListResourceRecordSetsRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListResourceRecordSetsResponse
            {
                ResourceRecordSets = new List<ResourceRecordSet>
                {
                    new()
                    {
                        Name = "example.com.",
                        Type = "CAA",  // not in our DnsRecordType enum — should be skipped
                        TTL = 3600,
                        ResourceRecords = new List<ResourceRecord> { new() { Value = "0 issue \"letsencrypt.org\"" } }
                    },
                    new()
                    {
                        Name = "www.example.com.",
                        Type = "A",
                        TTL = 300,
                        ResourceRecords = new List<ResourceRecord> { new() { Value = "1.2.3.4" } }
                    }
                }
            });

        var records = await sut.GetResourceRecordsAsync(ZoneName, node: null);

        records.Should().HaveCount(1);
        records[0].RecordType.Should().Be(DnsRecordType.A);
    }

    [Fact]
    public async Task GetResourceRecordsAsync_WithNode_SetsStartRecordName()
    {
        var (sut, client) = CreateSut();
        StubZoneLookup(client);

        ListResourceRecordSetsRequest? captured = null;
        client.Setup(c => c.ListResourceRecordSetsAsync(
                It.IsAny<ListResourceRecordSetsRequest>(),
                It.IsAny<CancellationToken>()))
            .Callback<ListResourceRecordSetsRequest, CancellationToken>((req, _) => captured = req)
            .ReturnsAsync(new ListResourceRecordSetsResponse { ResourceRecordSets = new List<ResourceRecordSet>() });

        await sut.GetResourceRecordsAsync(ZoneName, node: "www");

        captured.Should().NotBeNull();
        captured!.StartRecordName.Should().Be("www.example.com");
        captured.HostedZoneId.Should().Be(ZoneId);
    }

    // ── AddResourceRecordAsync ────────────────────────────────────────────────

    [Fact]
    public async Task AddResourceRecordAsync_IssuesUpsert()
    {
        var (sut, client) = CreateSut();
        StubZoneLookup(client);

        ChangeResourceRecordSetsRequest? captured = null;
        client.Setup(c => c.ChangeResourceRecordSetsAsync(
                It.IsAny<ChangeResourceRecordSetsRequest>(),
                It.IsAny<CancellationToken>()))
            .Callback<ChangeResourceRecordSetsRequest, CancellationToken>((req, _) => captured = req)
            .ReturnsAsync(new ChangeResourceRecordSetsResponse());

        await sut.AddResourceRecordAsync(new AddRecordRequest
        {
            ZoneName = ZoneName,
            HostName = "www",
            RecordType = DnsRecordType.A,
            Data = "10.0.0.1",
            TimeToLive = 120
        });

        captured.Should().NotBeNull();
        captured!.HostedZoneId.Should().Be(ZoneId);
        var change = captured.ChangeBatch.Changes.Single();
        change.Action.Should().Be(ChangeAction.UPSERT);
        change.ResourceRecordSet.Name.Should().Be("www.example.com");
        change.ResourceRecordSet.Type.Should().Be(RRType.A);
        change.ResourceRecordSet.TTL.Should().Be(120);
        change.ResourceRecordSet.ResourceRecords.Single().Value.Should().Be("10.0.0.1");
    }

    [Fact]
    public async Task AddResourceRecordAsync_TxtRecord_QuotesValue()
    {
        var (sut, client) = CreateSut();
        StubZoneLookup(client);

        ChangeResourceRecordSetsRequest? captured = null;
        client.Setup(c => c.ChangeResourceRecordSetsAsync(
                It.IsAny<ChangeResourceRecordSetsRequest>(),
                It.IsAny<CancellationToken>()))
            .Callback<ChangeResourceRecordSetsRequest, CancellationToken>((req, _) => captured = req)
            .ReturnsAsync(new ChangeResourceRecordSetsResponse());

        await sut.AddResourceRecordAsync(new AddRecordRequest
        {
            ZoneName = ZoneName,
            HostName = "_acme-challenge",
            RecordType = DnsRecordType.TXT,
            Data = "verification-token"
        });

        captured!.ChangeBatch.Changes.Single().ResourceRecordSet.ResourceRecords.Single().Value
            .Should().Be("\"verification-token\"");
    }

    // ── RemoveResourceRecordAsync ─────────────────────────────────────────────

    [Fact]
    public async Task RemoveResourceRecordAsync_IssuesDelete()
    {
        var (sut, client) = CreateSut();
        StubZoneLookup(client);

        ChangeResourceRecordSetsRequest? captured = null;
        client.Setup(c => c.ChangeResourceRecordSetsAsync(
                It.IsAny<ChangeResourceRecordSetsRequest>(),
                It.IsAny<CancellationToken>()))
            .Callback<ChangeResourceRecordSetsRequest, CancellationToken>((req, _) => captured = req)
            .ReturnsAsync(new ChangeResourceRecordSetsResponse());

        await sut.RemoveResourceRecordAsync(new DeleteRecordRequest
        {
            ZoneName = ZoneName,
            HostName = "www",
            RecordType = DnsRecordType.A,
            Data = "10.0.0.1"
        });

        var change = captured!.ChangeBatch.Changes.Single();
        change.Action.Should().Be(ChangeAction.DELETE);
        change.ResourceRecordSet.Name.Should().Be("www.example.com");
        change.ResourceRecordSet.ResourceRecords.Single().Value.Should().Be("10.0.0.1");
    }
}
