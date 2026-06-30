using System.Net;
using DnsGoBetween.Core.Models;
using DnsGoBetween.Infrastructure.Dns;
using DnsGoBetween.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace DnsGoBetween.Tests;

public class NamecheapDnsProviderTests
{
    private const string Ns = "http://api.namecheap.com/xml.response";

    private static HttpClient CreateClient(FakeHttpMessageHandler handler) =>
        new(handler) { BaseAddress = new Uri("https://api.namecheap.com/xml.response") };

    private static IConfiguration CreateConfig() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Dns:NamecheapApiUser"] = "testuser",
            ["Dns:NamecheapApiKey"] = "testkey",
            ["Dns:NamecheapClientIp"] = "127.0.0.1"
        })
        .Build();

    // ── Configuration validation ──────────────────────────────────────────────

    [Fact]
    public void Constructor_MissingApiUser_Throws()
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Dns:NamecheapApiKey"] = "k",
                ["Dns:NamecheapClientIp"] = "1.1.1.1"
            })
            .Build();

        var act = () => new NamecheapDnsProvider(CreateClient(handler), config);

        act.Should().Throw<ArgumentException>().WithMessage("*NamecheapApiUser*");
    }

    // ── GetZonesAsync ─────────────────────────────────────────────────────────

    [Fact]
    public async Task GetZonesAsync_ParsesDomainList()
    {
        var xml = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <ApiResponse Status="OK" xmlns="{Ns}">
              <CommandResponse>
                <DomainGetListResult>
                  <Domain Name="example.com" />
                  <Domain Name="another.net" />
                </DomainGetListResult>
              </CommandResponse>
            </ApiResponse>
            """;
        var handler = new FakeHttpMessageHandler(_ => FakeHttpMessageHandler.Xml(xml));
        var sut = new NamecheapDnsProvider(CreateClient(handler), CreateConfig());

        var zones = await sut.GetZonesAsync();

        zones.Should().HaveCount(2);
        zones[0].Name.Should().Be("example.com");
        zones[0].ZoneType.Should().Be("Primary");
        zones[0].IsDynamicUpdateEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task GetZonesAsync_PassesAuthAndCommandParameters()
    {
        const string xml = """<?xml version="1.0"?><ApiResponse Status="OK" xmlns="http://api.namecheap.com/xml.response"><CommandResponse><DomainGetListResult /></CommandResponse></ApiResponse>""";
        var handler = new FakeHttpMessageHandler(_ => FakeHttpMessageHandler.Xml(xml));
        var sut = new NamecheapDnsProvider(CreateClient(handler), CreateConfig());

        await sut.GetZonesAsync();

        var uri = handler.Requests[0].RequestUri!.ToString();
        uri.Should().Contain("ApiUser=testuser")
           .And.Contain("ApiKey=testkey")
           .And.Contain("ClientIp=127.0.0.1")
           .And.Contain("Command=namecheap.domains.getList");
    }

    // ── GetResourceRecordsAsync ───────────────────────────────────────────────

    [Fact]
    public async Task GetResourceRecordsAsync_ParsesHosts()
    {
        var xml = $"""
            <?xml version="1.0"?>
            <ApiResponse Status="OK" xmlns="{Ns}">
              <CommandResponse>
                <DomainDNSGetHostsResult>
                  <host Name="@" Type="A" Address="1.2.3.4" TTL="1800" />
                  <host Name="www" Type="A" Address="5.6.7.8" TTL="600" />
                  <host Name="legacy" Type="UNKNOWN" Address="x" TTL="60" />
                </DomainDNSGetHostsResult>
              </CommandResponse>
            </ApiResponse>
            """;
        var handler = new FakeHttpMessageHandler(_ => FakeHttpMessageHandler.Xml(xml));
        var sut = new NamecheapDnsProvider(CreateClient(handler), CreateConfig());

        var records = await sut.GetResourceRecordsAsync("example.com", node: null);

        records.Should().HaveCount(2);  // UNKNOWN type filtered out
        records[0].HostName.Should().Be("@");
        records[0].Data.Should().Be("1.2.3.4");
        records[0].TimeToLive.Should().Be(1800);
        records[1].HostName.Should().Be("www");
    }

    [Fact]
    public async Task GetResourceRecordsAsync_WithNode_FiltersResults()
    {
        var xml = $"""
            <?xml version="1.0"?>
            <ApiResponse Status="OK" xmlns="{Ns}">
              <CommandResponse>
                <DomainDNSGetHostsResult>
                  <host Name="www" Type="A" Address="1.1.1.1" TTL="600" />
                  <host Name="api" Type="A" Address="2.2.2.2" TTL="600" />
                </DomainDNSGetHostsResult>
              </CommandResponse>
            </ApiResponse>
            """;
        var handler = new FakeHttpMessageHandler(_ => FakeHttpMessageHandler.Xml(xml));
        var sut = new NamecheapDnsProvider(CreateClient(handler), CreateConfig());

        var records = await sut.GetResourceRecordsAsync("example.com", node: "api");

        records.Should().HaveCount(1);
        records[0].HostName.Should().Be("api");
        records[0].Data.Should().Be("2.2.2.2");
    }

    [Fact]
    public async Task GetResourceRecordsAsync_SendsSldAndTldSeparately()
    {
        const string xml = """<?xml version="1.0"?><ApiResponse Status="OK" xmlns="http://api.namecheap.com/xml.response"><CommandResponse><DomainDNSGetHostsResult /></CommandResponse></ApiResponse>""";
        var handler = new FakeHttpMessageHandler(_ => FakeHttpMessageHandler.Xml(xml));
        var sut = new NamecheapDnsProvider(CreateClient(handler), CreateConfig());

        await sut.GetResourceRecordsAsync("example.com", node: null);

        var uri = handler.Requests[0].RequestUri!.ToString();
        uri.Should().Contain("SLD=example").And.Contain("TLD=com");
    }

    // ── AddResourceRecordAsync ────────────────────────────────────────────────

    [Fact]
    public async Task AddResourceRecordAsync_AppendsRecordAndCallsSetHosts()
    {
        // First call (GET hosts) returns one existing record.
        const string existing = """
            <?xml version="1.0"?>
            <ApiResponse Status="OK" xmlns="http://api.namecheap.com/xml.response">
              <CommandResponse>
                <DomainDNSGetHostsResult>
                  <host Name="www" Type="A" Address="1.1.1.1" TTL="600" />
                </DomainDNSGetHostsResult>
              </CommandResponse>
            </ApiResponse>
            """;
        const string okResponse = """<?xml version="1.0"?><ApiResponse Status="OK" xmlns="http://api.namecheap.com/xml.response"><CommandResponse /></ApiResponse>""";

        var handler = new FakeHttpMessageHandler(req =>
            req.Method == HttpMethod.Get
                ? FakeHttpMessageHandler.Xml(existing)
                : FakeHttpMessageHandler.Xml(okResponse));

        var sut = new NamecheapDnsProvider(CreateClient(handler), CreateConfig());

        await sut.AddResourceRecordAsync(new AddRecordRequest
        {
            ZoneName = "example.com",
            HostName = "api",
            RecordType = DnsRecordType.A,
            Data = "2.2.2.2",
            TimeToLive = 300
        });

        var postBody = handler.RequestBodies.Single(b => b.Length > 0);
        postBody.Should().Contain("HostName1=www")
            .And.Contain("Address1=1.1.1.1")
            .And.Contain("HostName2=api")
            .And.Contain("Address2=2.2.2.2")
            .And.Contain("TTL2=300")
            .And.Contain("Command=namecheap.domains.dns.setHosts");
    }

    // ── RemoveResourceRecordAsync ─────────────────────────────────────────────

    [Fact]
    public async Task RemoveResourceRecordAsync_RemovesMatchingRecord()
    {
        const string existing = """
            <?xml version="1.0"?>
            <ApiResponse Status="OK" xmlns="http://api.namecheap.com/xml.response">
              <CommandResponse>
                <DomainDNSGetHostsResult>
                  <host Name="www" Type="A" Address="1.1.1.1" TTL="600" />
                  <host Name="api" Type="A" Address="2.2.2.2" TTL="600" />
                </DomainDNSGetHostsResult>
              </CommandResponse>
            </ApiResponse>
            """;
        const string okResponse = """<?xml version="1.0"?><ApiResponse Status="OK" xmlns="http://api.namecheap.com/xml.response"><CommandResponse /></ApiResponse>""";

        var handler = new FakeHttpMessageHandler(req =>
            req.Method == HttpMethod.Get
                ? FakeHttpMessageHandler.Xml(existing)
                : FakeHttpMessageHandler.Xml(okResponse));

        var sut = new NamecheapDnsProvider(CreateClient(handler), CreateConfig());

        await sut.RemoveResourceRecordAsync(new DeleteRecordRequest
        {
            ZoneName = "example.com",
            HostName = "api",
            RecordType = DnsRecordType.A,
            Data = "2.2.2.2"
        });

        var postBody = handler.RequestBodies.Single(b => b.Length > 0);
        postBody.Should().Contain("HostName1=www")
            .And.NotContain("HostName2=");  // only one record remains
    }

    [Fact]
    public async Task RemoveResourceRecordAsync_NoMatch_Throws()
    {
        const string existing = """
            <?xml version="1.0"?>
            <ApiResponse Status="OK" xmlns="http://api.namecheap.com/xml.response">
              <CommandResponse>
                <DomainDNSGetHostsResult>
                  <host Name="www" Type="A" Address="1.1.1.1" TTL="600" />
                </DomainDNSGetHostsResult>
              </CommandResponse>
            </ApiResponse>
            """;
        var handler = new FakeHttpMessageHandler(_ => FakeHttpMessageHandler.Xml(existing));
        var sut = new NamecheapDnsProvider(CreateClient(handler), CreateConfig());

        var act = () => sut.RemoveResourceRecordAsync(new DeleteRecordRequest
        {
            ZoneName = "example.com",
            HostName = "missing",
            RecordType = DnsRecordType.A,
            Data = "9.9.9.9"
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*No exact DNS record match*");
    }

    [Fact]
    public async Task SetHosts_ApiErrorStatus_Throws()
    {
        const string existing = """<?xml version="1.0"?><ApiResponse Status="OK" xmlns="http://api.namecheap.com/xml.response"><CommandResponse><DomainDNSGetHostsResult /></CommandResponse></ApiResponse>""";
        const string errorResponse = """<?xml version="1.0"?><ApiResponse Status="ERROR" xmlns="http://api.namecheap.com/xml.response"><Errors><Error Number="2030280">Invalid key</Error></Errors></ApiResponse>""";

        var handler = new FakeHttpMessageHandler(req =>
            req.Method == HttpMethod.Get
                ? FakeHttpMessageHandler.Xml(existing)
                : FakeHttpMessageHandler.Xml(errorResponse));

        var sut = new NamecheapDnsProvider(CreateClient(handler), CreateConfig());

        var act = () => sut.AddResourceRecordAsync(new AddRecordRequest
        {
            ZoneName = "example.com",
            HostName = "api",
            RecordType = DnsRecordType.A,
            Data = "2.2.2.2"
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Namecheap*rejected*");
    }
}
