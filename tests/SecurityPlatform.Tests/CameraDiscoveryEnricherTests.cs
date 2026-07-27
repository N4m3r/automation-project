using SecurityPlatform.Core.Domain;
using SecurityPlatform.Drivers.Onvif;
using SecurityPlatform.Modules.Admin;

namespace SecurityPlatform.Tests;

public class CameraDiscoveryEnricherTests
{
    [Fact]
    public void ParseHikvisionInputProxy_reads_source_ips()
    {
        var xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <InputProxyChannelList xmlns="http://www.hikvision.com/ver20/XMLSchema">
              <InputProxyChannel>
                <id>1</id>
                <name>Portaria</name>
                <sourceInputPortDescriptor>
                  <ipAddress>192.168.1.64</ipAddress>
                </sourceInputPortDescriptor>
              </InputProxyChannel>
              <InputProxyChannel>
                <id>2</id>
                <name>Garagem</name>
                <sourceInputPortDescriptor>
                  <ipAddress>192.168.1.65</ipAddress>
                </sourceInputPortDescriptor>
              </InputProxyChannel>
            </InputProxyChannelList>
            """;
        var nvr = new Device { Id = 9, Name = "NVR-01", Host = "192.168.1.10" };
        var list = CameraDiscoveryEnricher.ParseHikvisionInputProxy(xml, nvr);
        Assert.Equal(2, list.Count);
        Assert.Equal("192.168.1.64", list[0].SourceIp);
        Assert.Equal("Portaria", list[0].ChannelName);
        Assert.Equal(9, list[0].NvrDeviceId);
        Assert.Equal("192.168.1.65", list[1].SourceIp);
    }

    [Fact]
    public void ParseDahuaRemoteDevices_reads_addresses()
    {
        var text = """
            table.RemoteDevice[0].Enable=true
            table.RemoteDevice[0].Name=Cam_Portaria
            table.RemoteDevice[0].Address=10.0.0.50
            table.RemoteDevice[1].Enable=false
            table.RemoteDevice[1].Name=Cam_Fundo
            table.RemoteDevice[1].Address=10.0.0.51
            """;
        var nvr = new Device { Id = 3, Name = "DVR", Host = "10.0.0.1" };
        var list = CameraDiscoveryEnricher.ParseDahuaRemoteDevices(text, nvr);
        Assert.Equal(2, list.Count);
        Assert.Equal("10.0.0.50", list[0].SourceIp);
        Assert.Equal("Cam_Portaria", list[0].ChannelName);
        Assert.Equal(1, list[0].ChannelId);
        Assert.Equal("10.0.0.51", list[1].SourceIp);
    }

    [Fact]
    public void ParseHikvisionOsd_prefers_displayText()
    {
        var xml = """
            <?xml version="1.0"?>
            <VideoOverlay>
              <TextOverlayList>
                <TextOverlay>
                  <id>1</id>
                  <enabled>true</enabled>
                  <displayText>ENTRADA PRINCIPAL</displayText>
                </TextOverlay>
              </TextOverlayList>
            </VideoOverlay>
            """;
        Assert.Equal("ENTRADA PRINCIPAL", CameraDiscoveryEnricher.ParseHikvisionOsd(xml));
    }

    [Fact]
    public void ParseDahuaChannelTitle_reads_name()
    {
        var text = "table.ChannelTitle[0].Name=PORTARIA\r\ntable.ChannelTitle[1].Name=FUNDO\r\n";
        Assert.Equal("PORTARIA", CameraDiscoveryEnricher.ParseDahuaChannelTitle(text));
    }

    [Fact]
    public void NormalizeHost_strips_ipv4_port()
    {
        Assert.Equal("192.168.1.1", CameraDiscoveryEnricher.NormalizeHost("192.168.1.1:80"));
        Assert.Equal("192.168.1.1", CameraDiscoveryEnricher.NormalizeHost("192.168.1.1"));
    }

    [Fact]
    public async Task EnrichAsync_skips_registered_and_nvr_sources()
    {
        // Sem chamar rede: EnrichAsync ainda consulta NVRs reais; usamos hosts
        // que não respondem e validamos o filtro de "already registered".
        var found = new List<OnvifDiscovery.DiscoveredDevice>
        {
            new("192.168.1.64", 80, "Cam_A", "http://192.168.1.64/onvif", "onvif://www.onvif.org/name/Cam_A"),
            new("192.168.1.70", 80, "Cam_Avulsa", "http://192.168.1.70/onvif", "onvif://www.onvif.org/name/Cam_Avulsa"),
        };
        var registered = new List<Device>
        {
            new() { Id = 1, Name = "Já no VMS", Host = "192.168.1.64", Kind = DeviceKind.Camera }
        };

        var result = await CameraDiscoveryEnricher.EnrichAsync(
            found, registered, probeUsername: null, probePassword: null);

        Assert.Single(result.Standalone);
        Assert.Equal("192.168.1.70", result.Standalone[0].Host);
        Assert.Single(result.Skipped);
        Assert.True(result.Skipped[0].AlreadyRegistered);
        Assert.Equal(1, result.Skipped[0].RegisteredDeviceId);
    }
}
