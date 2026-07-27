using System.Xml.Linq;
using SecurityPlatform.Drivers.Onvif;
using SecurityPlatform.Modules.Vms;

namespace SecurityPlatform.Tests;

public class OnvifPtzAndDiscoveryTests
{
    [Fact]
    public void PasswordDigest_is_stable_for_known_inputs()
    {
        var nonce = Convert.FromBase64String("LKqI6G/AikKCQrN0zqZFlg==");
        var created = "2010-09-16T07:50:45Z";
        var digest = OnvifSoap.PasswordDigest(nonce, created, "userpassword");
        Assert.False(string.IsNullOrWhiteSpace(digest));
        Assert.Equal(digest, OnvifSoap.PasswordDigest(nonce, created, "userpassword"));
    }

    [Fact]
    public void ParsePresets_reads_token_and_name()
    {
        var xml = """
            <?xml version="1.0"?>
            <s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope"
                        xmlns:tt="http://www.onvif.org/ver10/schema"
                        xmlns:tptz="http://www.onvif.org/ver20/ptz/wsdl">
              <s:Body>
                <tptz:GetPresetsResponse>
                  <tptz:Preset token="1">
                    <tt:Name>Entrada</tt:Name>
                  </tptz:Preset>
                  <tptz:Preset token="2">
                    <tt:Name>Garagem</tt:Name>
                  </tptz:Preset>
                </tptz:GetPresetsResponse>
              </s:Body>
            </s:Envelope>
            """;
        var map = OnvifPtzClient.ParsePresets(XDocument.Parse(xml));
        Assert.Equal(2, map.Count);
        Assert.Equal("Entrada", map["1"]);
        Assert.Equal("Garagem", map["2"]);
    }

    [Fact]
    public void ParseProbeMatches_extracts_host()
    {
        var xml = """
            <?xml version="1.0"?>
            <e:Envelope xmlns:e="http://www.w3.org/2003/05/soap-envelope"
                        xmlns:d="http://schemas.xmlsoap.org/ws/2005/04/discovery"
                        xmlns:w="http://schemas.xmlsoap.org/ws/2004/08/addressing">
              <e:Body>
                <d:ProbeMatches>
                  <d:ProbeMatch>
                    <w:EndpointReference><w:Address>urn:uuid:abc</w:Address></w:EndpointReference>
                    <d:Types>dn:NetworkVideoTransmitter</d:Types>
                    <d:Scopes>onvif://www.onvif.org/name/Cam_Portaria</d:Scopes>
                    <d:XAddrs>http://192.168.1.50:80/onvif/device_service</d:XAddrs>
                    <d:MetadataVersion>1</d:MetadataVersion>
                  </d:ProbeMatch>
                </d:ProbeMatches>
              </e:Body>
            </e:Envelope>
            """;
        var list = OnvifDiscovery.ParseProbeMatches(xml).ToList();
        Assert.Single(list);
        Assert.Equal("192.168.1.50", list[0].Host);
        Assert.Equal(80, list[0].Port);
        Assert.Contains("Portaria", list[0].Name, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryParseStats_reads_fps_and_bitrate()
    {
        var line = "frame=  120 fps= 25.0 q=-1.0 size=    1024kB time=00:00:04.80 bitrate=1747.6kbits/s speed=1.0x";
        Assert.True(RecorderService.TryParseStats(line, out var st));
        Assert.Equal(25.0, st.Fps);
        Assert.Equal(1747.6, st.BitrateKbps);
    }

    [Fact]
    public void AppendQuery_keeps_path_before_query()
    {
        var url = VmsEndpoints.AppendQuery("http://host:8889/cam1", "jwt", "abc");
        Assert.Equal("http://host:8889/cam1?jwt=abc", url);
        var again = VmsEndpoints.AppendQuery(url, "x", "1");
        Assert.Equal("http://host:8889/cam1?jwt=abc&x=1", again);
    }
}
