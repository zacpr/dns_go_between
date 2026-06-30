using System.Management.Automation;
using DnsGoBetween.Core.Models;
using DnsGoBetween.Infrastructure.Dns;
using Xunit;

namespace DnsGoBetween.Tests;

public class RemoteWindowsDnsProviderTests
{
    [Fact]
    public void ParseRecords_ParsesARecordCorrectly()
    {
        // Arrange
        var pso = new PSObject();
        pso.Properties.Add(new PSNoteProperty("HostName", "test"));
        pso.Properties.Add(new PSNoteProperty("RecordType", "A"));
        pso.Properties.Add(new PSNoteProperty("TimeToLive", TimeSpan.FromSeconds(300)));
        
        var recordData = new PSObject();
        recordData.Properties.Add(new PSNoteProperty("IPv4Address", "192.168.1.50"));
        pso.Properties.Add(new PSNoteProperty("RecordData", recordData));

        var results = new List<PSObject> { pso };

        // Act
        var records = RemoteWindowsDnsProvider.ParseRecords("example.com", results);

        // Assert
        Assert.Single(records);
        Assert.Equal("test", records[0].HostName);
        Assert.Equal(DnsRecordType.A, records[0].RecordType);
        Assert.Equal("192.168.1.50", records[0].Data);
        Assert.Equal(300, records[0].TimeToLive);
        Assert.Equal("example.com", records[0].ZoneName);
    }

    [Fact]
    public void ParseRecords_ParsesTxtRecordWithArrayDescriptiveText()
    {
        // Arrange
        var pso = new PSObject();
        pso.Properties.Add(new PSNoteProperty("HostName", "_acme-challenge"));
        pso.Properties.Add(new PSNoteProperty("RecordType", "TXT"));
        pso.Properties.Add(new PSNoteProperty("TimeToLive", TimeSpan.FromSeconds(60)));
        
        var recordData = new PSObject();
        // Route53 and PowerShell often wrap TXT values differently. We test the array fallback here.
        recordData.Properties.Add(new PSNoteProperty("DescriptiveText", new[] { "part1", "part2" }));
        pso.Properties.Add(new PSNoteProperty("RecordData", recordData));

        var results = new List<PSObject> { pso };

        // Act
        var records = RemoteWindowsDnsProvider.ParseRecords("example.com", results);

        // Assert
        Assert.Single(records);
        Assert.Equal(DnsRecordType.TXT, records[0].RecordType);
        Assert.Equal("part1part2", records[0].Data);
    }

    [Fact]
    public void ParseZones_ParsesDynamicUpdateStatesCorrectly()
    {
        // Arrange
        var pso1 = new PSObject();
        pso1.Properties.Add(new PSNoteProperty("ZoneName", "secure.zone"));
        pso1.Properties.Add(new PSNoteProperty("ZoneType", "Primary"));
        pso1.Properties.Add(new PSNoteProperty("DynamicUpdate", "Secure"));

        var pso2 = new PSObject();
        pso2.Properties.Add(new PSNoteProperty("ZoneName", "static.zone"));
        pso2.Properties.Add(new PSNoteProperty("ZoneType", "Primary"));
        pso2.Properties.Add(new PSNoteProperty("DynamicUpdate", "None"));

        var results = new List<PSObject> { pso1, pso2 };

        // Act
        var zones = RemoteWindowsDnsProvider.ParseZones(results);

        // Assert
        Assert.Equal(2, zones.Count);
        Assert.True(zones.First(z => z.Name == "secure.zone").IsDynamicUpdateEnabled);
        Assert.False(zones.First(z => z.Name == "static.zone").IsDynamicUpdateEnabled);
    }

    // ── Helper Extraction Tests ───────────────────────────────────────────────

    [Fact]
    public void GetStringProperty_MissingOrNull_ReturnsEmptyString()
    {
        var pso = new PSObject();
        
        Assert.Equal(string.Empty, RemoteWindowsDnsProvider.GetStringProperty(pso, "Missing"));
        Assert.Equal(string.Empty, RemoteWindowsDnsProvider.GetStringProperty(null, "Missing"));
    }

    [Fact]
    public void GetStringArrayProperty_MissingOrNull_ReturnsEmptyString()
    {
        var pso = new PSObject();
        
        Assert.Equal(string.Empty, RemoteWindowsDnsProvider.GetStringArrayProperty(pso, "Missing"));
        Assert.Equal(string.Empty, RemoteWindowsDnsProvider.GetStringArrayProperty(null, "Missing"));
    }

    [Fact]
    public void GetStringArrayProperty_SingleString_ReturnsString()
    {
        var pso = new PSObject();
        pso.Properties.Add(new PSNoteProperty("DescriptiveText", "SingleValue"));
        
        var result = RemoteWindowsDnsProvider.GetStringArrayProperty(pso, "DescriptiveText");
        Assert.Equal("SingleValue", result);
    }

    [Fact]
    public void GetStringArrayProperty_StringArray_ReturnsConcatenatedString()
    {
        var pso = new PSObject();
        pso.Properties.Add(new PSNoteProperty("DescriptiveText", new[] { "part1", "part2" }));
        
        var result = RemoteWindowsDnsProvider.GetStringArrayProperty(pso, "DescriptiveText");
        Assert.Equal("part1part2", result);
    }

    [Fact]
    public void GetNestedObject_MissingOrNull_ReturnsNull()
    {
        var pso = new PSObject();
        
        Assert.Null(RemoteWindowsDnsProvider.GetNestedObject(pso, "Missing"));
        Assert.Null(RemoteWindowsDnsProvider.GetNestedObject(null, "Missing"));
    }

    [Fact]
    public void GetNestedObject_RawObject_ReturnsWrappedPSObject()
    {
        var pso = new PSObject();
        // Simulate a raw .NET or CIM object that isn't inherently a PSObject
        var rawData = new { IPv4Address = "10.0.0.1" };
        pso.Properties.Add(new PSNoteProperty("RecordData", rawData));
        
        var result = RemoteWindowsDnsProvider.GetNestedObject(pso, "RecordData");
        
        Assert.NotNull(result);
        Assert.Equal("10.0.0.1", result.Properties["IPv4Address"]?.Value?.ToString());
    }

    [Fact]
    public void GetTtlProperty_MissingOrNull_ReturnsFallback()
    {
        var pso = new PSObject();
        
        Assert.Equal(1234, RemoteWindowsDnsProvider.GetTtlProperty(pso, "TimeToLive", 1234));
        Assert.Equal(1234, RemoteWindowsDnsProvider.GetTtlProperty(null, "TimeToLive", 1234));
    }

    [Fact]
    public void GetTtlProperty_TimeSpanValue_ReturnsTotalSeconds()
    {
        var pso = new PSObject();
        pso.Properties.Add(new PSNoteProperty("TimeToLive", TimeSpan.FromMinutes(5)));
        
        var result = RemoteWindowsDnsProvider.GetTtlProperty(pso, "TimeToLive");
        Assert.Equal(300, result);
    }

    [Fact]
    public void GetTtlProperty_StringTimeSpanValue_ReturnsTotalSeconds()
    {
        var pso = new PSObject();
        pso.Properties.Add(new PSNoteProperty("TimeToLive", "00:10:00")); // 10 minutes
        
        var result = RemoteWindowsDnsProvider.GetTtlProperty(pso, "TimeToLive");
        Assert.Equal(600, result);
    }
}