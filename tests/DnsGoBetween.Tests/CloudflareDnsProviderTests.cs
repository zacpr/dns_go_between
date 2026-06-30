using System.Net;
using DnsGoBetween.Core.Models;
using DnsGoBetween.Infrastructure.Dns;
using DnsGoBetween.Tests.Helpers;
using FluentAssertions;
using Xunit;

namespace DnsGoBetween.Tests;

public class CloudflareDnsProviderTests
{
    private const string ZoneId = "abc123zone";
    private const string ZoneName = "example.com";

    private static HttpClient CreateClient(FakeHttpMessageHandler handler) =>
        new(handler) { BaseAddress = new Uri("https://api.cloudflare.com/client/v4/") };

    // ── GetZonesAsync ─────────────────────────────────────────────────────────

    [Fact]
    public async Task GetZonesAsync_ReturnsParsedZones()
    {
        const string json = """
            {"result":[
                {"id":"abc","name":"example.com"},
                {"id":"def","name":"another.net"}
            ]}
            """;
        var handler = new FakeHttpMessageHandler(_ => FakeHttpMessageHandler.Json(json));
        var sut = new CloudflareDnsProvider(CreateClient(handler));

        var zones = await sut.GetZonesAsync();

        zones.Should().HaveCount(2);
        zones[0].Name.Should().Be("example.com");
        zones[0].ZoneType.Should().Be("Primary");
        zones[0].IsDynamicUpdateEnabled.Should().BeTrue();
        zones[1].Name.Should().Be("another.net");
        handler.Requests[0].RequestUri!.AbsolutePath.Should().EndWith("/zones");
    }

    [Fact]
    public async Task GetZonesAsync_EmptyResult_ReturnsEmptyList()
    {
        var handler = new FakeHttpMessageHandler(_ => FakeHttpMessageHandler.Json("""{"result":[]}"""));
        var sut = new CloudflareDnsProvider(CreateClient(handler));

        var zones = await sut.GetZonesAsync();

        zones.Should().BeEmpty();
    }

    // ── GetResourceRecordsAsync ───────────────────────────────────────────────

    [Fact]
    public async Task GetResourceRecordsAsync_ResolvesZoneIdThenFetchesRecords()
    {
        const string zoneLookup = """{"result":[{"id":"abc123zone","name":"example.com"}]}""";
        const string records = """
            {"result":[
                {"id":"r1","type":"A","name":"www.example.com","content":"1.2.3.4","ttl":300},
                {"id":"r2","type":"A","name":"example.com","content":"1.2.3.5","ttl":600}
            ]}
            """;

        var handler = new FakeHttpMessageHandler(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("zones?name="))
                return FakeHttpMessageHandler.Json(zoneLookup);
            if (url.Contains($"zones/{ZoneId}/dns_records"))
                return FakeHttpMessageHandler.Json(records);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var sut = new CloudflareDnsProvider(CreateClient(handler));

        var result = await sut.GetResourceRecordsAsync(ZoneName, node: null);

        result.Should().HaveCount(2);
        result[0].HostName.Should().Be("www");
        result[0].Data.Should().Be("1.2.3.4");
        result[0].TimeToLive.Should().Be(300);
        result[0].RecordType.Should().Be(DnsRecordType.A);
        result[1].HostName.Should().Be("@");  // apex normalized
    }

    [Fact]
    public async Task GetResourceRecordsAsync_WithNode_AppendsNameFilter()
    {
        var handler = new FakeHttpMessageHandler(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("zones?name="))
                return FakeHttpMessageHandler.Json($$"""{"result":[{"id":"{{ZoneId}}","name":"{{ZoneName}}"}]}""");
            return FakeHttpMessageHandler.Json("""{"result":[]}""");
        });
        var sut = new CloudflareDnsProvider(CreateClient(handler));

        await sut.GetResourceRecordsAsync(ZoneName, node: "www");

        handler.Requests[1].RequestUri!.ToString()
            .Should().Contain("name=www.example.com");
    }

    [Fact]
    public async Task GetResourceRecordsAsync_NodeIsApex_UsesZoneNameAsFqdn()
    {
        var handler = new FakeHttpMessageHandler(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("zones?name="))
                return FakeHttpMessageHandler.Json($$"""{"result":[{"id":"{{ZoneId}}","name":"{{ZoneName}}"}]}""");
            return FakeHttpMessageHandler.Json("""{"result":[]}""");
        });
        var sut = new CloudflareDnsProvider(CreateClient(handler));

        await sut.GetResourceRecordsAsync(ZoneName, node: "@");

        handler.Requests[1].RequestUri!.ToString()
            .Should().Contain("name=example.com")
            .And.NotContain("name=%40");
    }

    [Fact]
    public async Task GetResourceRecordsAsync_ZoneNotFound_ReturnsEmpty()
    {
        var handler = new FakeHttpMessageHandler(_ => FakeHttpMessageHandler.Json("""{"result":[]}"""));
        var sut = new CloudflareDnsProvider(CreateClient(handler));

        var result = await sut.GetResourceRecordsAsync(ZoneName, node: null);

        result.Should().BeEmpty();
    }

    // ── AddResourceRecordAsync ────────────────────────────────────────────────

    [Fact]
    public async Task AddResourceRecordAsync_PostsCorrectPayload()
    {
        var handler = new FakeHttpMessageHandler(req =>
        {
            if (req.Method == HttpMethod.Get)
                return FakeHttpMessageHandler.Json($$"""{"result":[{"id":"{{ZoneId}}","name":"{{ZoneName}}"}]}""");
            return FakeHttpMessageHandler.Json("""{"success":true}""", HttpStatusCode.OK);
        });
        var sut = new CloudflareDnsProvider(CreateClient(handler));

        await sut.AddResourceRecordAsync(new AddRecordRequest
        {
            ZoneName = ZoneName,
            HostName = "www",
            RecordType = DnsRecordType.A,
            Data = "10.0.0.1",
            TimeToLive = 120
        });

        var postRequest = handler.Requests.Single(r => r.Method == HttpMethod.Post);
        postRequest.RequestUri!.ToString().Should().Contain($"zones/{ZoneId}/dns_records");

        var body = handler.RequestBodies.Last();
        body.Should().Contain("\"type\":\"A\"");
        body.Should().Contain("\"name\":\"www.example.com\"");
        body.Should().Contain("\"content\":\"10.0.0.1\"");
        body.Should().Contain("\"ttl\":120");
    }

    [Fact]
    public async Task AddResourceRecordAsync_ApexHost_UsesZoneNameAsFqdn()
    {
        var handler = new FakeHttpMessageHandler(req =>
        {
            if (req.Method == HttpMethod.Get)
                return FakeHttpMessageHandler.Json($$"""{"result":[{"id":"{{ZoneId}}","name":"{{ZoneName}}"}]}""");
            return FakeHttpMessageHandler.Json("""{"success":true}""");
        });
        var sut = new CloudflareDnsProvider(CreateClient(handler));

        await sut.AddResourceRecordAsync(new AddRecordRequest
        {
            ZoneName = ZoneName,
            HostName = "@",
            RecordType = DnsRecordType.A,
            Data = "10.0.0.1"
        });

        handler.RequestBodies.Last().Should().Contain("\"name\":\"example.com\"");
    }

    [Fact]
    public async Task AddResourceRecordAsync_HttpFailure_Throws()
    {
        var handler = new FakeHttpMessageHandler(req =>
        {
            if (req.Method == HttpMethod.Get)
                return FakeHttpMessageHandler.Json($$"""{"result":[{"id":"{{ZoneId}}","name":"{{ZoneName}}"}]}""");
            return new HttpResponseMessage(HttpStatusCode.BadRequest);
        });
        var sut = new CloudflareDnsProvider(CreateClient(handler));

        var act = () => sut.AddResourceRecordAsync(new AddRecordRequest
        {
            ZoneName = ZoneName,
            HostName = "www",
            RecordType = DnsRecordType.A,
            Data = "10.0.0.1"
        });

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    // ── RemoveResourceRecordAsync ─────────────────────────────────────────────

    [Fact]
    public async Task RemoveResourceRecordAsync_MatchesByDataAndDeletes()
    {
        const string lookup = """
            {"result":[
                {"id":"rec-keep","type":"A","name":"www.example.com","content":"1.1.1.1","ttl":300},
                {"id":"rec-target","type":"A","name":"www.example.com","content":"2.2.2.2","ttl":300}
            ]}
            """;

        var handler = new FakeHttpMessageHandler(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("zones?name="))
                return FakeHttpMessageHandler.Json($$"""{"result":[{"id":"{{ZoneId}}","name":"{{ZoneName}}"}]}""");
            if (req.Method == HttpMethod.Get)
                return FakeHttpMessageHandler.Json(lookup);
            return FakeHttpMessageHandler.Json("""{"success":true}""");
        });
        var sut = new CloudflareDnsProvider(CreateClient(handler));

        await sut.RemoveResourceRecordAsync(new DeleteRecordRequest
        {
            ZoneName = ZoneName,
            HostName = "www",
            RecordType = DnsRecordType.A,
            Data = "2.2.2.2"
        });

        var deleteRequest = handler.Requests.Single(r => r.Method == HttpMethod.Delete);
        deleteRequest.RequestUri!.ToString().Should().EndWith("/dns_records/rec-target");
    }

    [Fact]
    public async Task RemoveResourceRecordAsync_NoMatch_Throws()
    {
        var handler = new FakeHttpMessageHandler(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("zones?name="))
                return FakeHttpMessageHandler.Json($$"""{"result":[{"id":"{{ZoneId}}","name":"{{ZoneName}}"}]}""");
            return FakeHttpMessageHandler.Json("""
                {"result":[{"id":"rec-other","type":"A","name":"www.example.com","content":"9.9.9.9","ttl":300}]}
                """);
        });
        var sut = new CloudflareDnsProvider(CreateClient(handler));

        var act = () => sut.RemoveResourceRecordAsync(new DeleteRecordRequest
        {
            ZoneName = ZoneName,
            HostName = "www",
            RecordType = DnsRecordType.A,
            Data = "2.2.2.2"
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*No exact DNS record match*");
    }
}
