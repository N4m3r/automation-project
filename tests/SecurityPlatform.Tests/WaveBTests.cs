using SecurityPlatform.Core.Domain;
using SecurityPlatform.Core.Drivers;
using SecurityPlatform.Drivers.Vendors;
using SecurityPlatform.Modules.Vms;

namespace SecurityPlatform.Tests;

public class WaveBTests
{
    [Fact]
    public void Vendor_drivers_have_distinct_names()
    {
        IDeviceDriver[] d = [new DahuaDriver(), new IntelbrasDriver(), new AxisDriver()];
        Assert.Equal(3, d.Select(x => x.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(d, x => Assert.Contains(DeviceKind.Camera, x.Supports));
    }

    [Fact]
    public async Task Dahua_stream_url_uses_realmonitor()
    {
        var cam = new Device { Host = "10.0.0.5", Username = "admin", Password = "x", Driver = "dahua" };
        var url = await new DahuaDriver().GetStreamUrlAsync(cam);
        Assert.Contains("realmonitor", url);
        Assert.Contains("10.0.0.5", url);
    }

    [Fact]
    public async Task Axis_stream_url_uses_axis_media()
    {
        var cam = new Device { Host = "10.0.0.6", Username = "root", Password = "p", Driver = "axis" };
        var url = await new AxisDriver().GetStreamUrlAsync(cam);
        Assert.Contains("axis-media", url);
    }

    [Fact]
    public async Task Edge_playback_url_hikvision_has_starttime()
    {
        var cam = new Device
        {
            Host = "192.168.1.10", Username = "admin", Password = "pwd",
            Driver = "hikvision", Name = "Cam Hik"
        };
        var from = new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);
        var to = from.AddMinutes(10);
        // registry not used for hikvision branch
        var url = await EdgePullService.BuildPlaybackUrlAsync(
            cam, new DriverRegistry(Array.Empty<IDeviceDriver>()), from, to, default);
        Assert.NotNull(url);
        Assert.Contains("starttime=", url);
        Assert.Contains("Streaming/tracks", url);
    }

    [Fact]
    public async Task Edge_playback_url_dahua_uses_playback_path()
    {
        var cam = new Device
        {
            Host = "192.168.1.20", Username = "admin", Password = "pwd",
            Driver = "dahua", Name = "Dahua 1"
        };
        var from = new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);
        var to = from.AddMinutes(5);
        var url = await EdgePullService.BuildPlaybackUrlAsync(
            cam, new DriverRegistry(Array.Empty<IDeviceDriver>()), from, to, default);
        Assert.NotNull(url);
        Assert.Contains("playback", url);
    }

    [Fact]
    public void VmsOptions_OwnsDevice_and_NodeId()
    {
        var o = new VmsOptions { ShardCount = 2, ShardIndex = 1, NodeId = "node-a" };
        Assert.True(o.OwnsDevice(1));
        Assert.False(o.OwnsDevice(2));
        Assert.Equal("node-a", o.ResolveNodeId());
    }

    [Fact]
    public async Task Intelbras_inherits_dahua_stream_url()
    {
        var cam = new Device { Host = "10.0.0.9", Username = "admin", Password = "x", Driver = "intelbras" };
        var url = await new IntelbrasDriver().GetStreamUrlAsync(cam);
        Assert.Contains("realmonitor", url);
        Assert.Contains("10.0.0.9", url);
    }

    [Fact]
    public void Dahua_parses_real_presets_from_getPresets()
    {
        const string body =
            "list.preset[0].Name=Entrada\r\n" +
            "list.preset[0].PresetID=1\r\n" +
            "list.preset[1].Name=Portao\r\n" +
            "list.preset[1].PresetID=2\r\n";
        var map = DahuaDriver.ParsePresets(body);
        Assert.Equal(2, map.Count);
        Assert.Equal("Entrada", map["1"]);
        Assert.Equal("Portao", map["2"]);
    }

    [Fact]
    public void Dahua_presets_fallback_name_when_missing()
    {
        const string body = "list.preset[4].PresetID=7\r\n";
        var map = DahuaDriver.ParsePresets(body);
        Assert.Equal("Preset 7", map["7"]);
    }

    [Fact]
    public void TranscodePathName_suffix()
    {
        Assert.Equal("cam12tc", LiveTranscodeService.TranscodePathName(12));
    }

    [Fact]
    public void Lease_key_format()
    {
        Assert.Equal("cam:7", RecorderLeaseService.Key(7));
    }

    [Fact]
    public void Retention_parse_edge_prefix()
    {
        var t = RetentionService.ParseStart("edge_20260701_120000");
        Assert.NotNull(t);
        Assert.Equal(2026, t!.Value.Year);
        Assert.Equal(7, t.Value.Month);
    }
}
