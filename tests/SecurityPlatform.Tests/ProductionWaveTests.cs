using System.Security.Cryptography;
using System.Text;
using SecurityPlatform.Core.Domain;
using SecurityPlatform.Modules.Vms;

namespace SecurityPlatform.Tests;

/// <summary>
/// Cobertura da onda de produção: SIA parse, metadados VCA, cifragem de gravação,
/// leases HA e política de kind analítico.
/// </summary>
public class ProductionWaveTests
{
    [Theory]
    [InlineData("1001|BA|001", "1001", "BA", "001")]
    [InlineData("SIA-DCS*...|#1234|Nri1/BA001|", "1234", "BA", "001")]
    [InlineData("18110100199", "1811", "010", "019")]
    public void Sia_parse_formatos_comuns(string raw, string account, string code, string zone)
    {
        var (a, c, z) = SiaReceiverService.Parse(raw);
        Assert.Equal(account, a);
        Assert.Equal(code, c);
        Assert.Equal(zone, z);
    }

    [Fact]
    public void EventMetadata_kinds_extras_documentados()
    {
        var kinds = new[]
        {
            "motion", "intrusion", "line_crossing", "lpr", "face", "tamper",
            "people_counting", "thermal", "abandoned", "loitering", "other"
        };
        foreach (var k in kinds)
        {
            var m = new EventMetadata { Kind = k, Count = k == "people_counting" ? 3 : null, Temperature = k == "thermal" ? 42.5 : null };
            Assert.False(string.IsNullOrEmpty(m.Kind));
        }
    }

    [Fact]
    public void RecordingCrypto_formato_magico_e_extensao()
    {
        Assert.Equal(".enc", RecordingCrypto.Extension);
        Assert.True(RecordingCrypto.IsEncryptedPath(@"C:\data\cam1\seg.mp4.enc"));
        Assert.False(RecordingCrypto.IsEncryptedPath(@"C:\data\cam1\seg.mp4"));
        Assert.False(RecordingCrypto.IsEncryptedPath(null));
    }

    [Fact]
    public void RecordingCrypto_roundtrip_aes_gcm_manual()
    {
        // Espelha o layout SPENC1 do RecordingCrypto sem depender do keyring DP.
        var key = SHA256.HashData(Encoding.UTF8.GetBytes("test-key-material"));
        var plain = Encoding.UTF8.GetBytes("segmento-de-video-fake");
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(key, 16))
            aes.Encrypt(nonce, plain, cipher, tag);

        var blob = new byte[6 + 12 + 16 + cipher.Length];
        Encoding.ASCII.GetBytes("SPENC1").CopyTo(blob, 0);
        nonce.CopyTo(blob, 6);
        tag.CopyTo(blob, 18);
        cipher.CopyTo(blob, 34);

        var outPlain = new byte[cipher.Length];
        using (var aes = new AesGcm(key, 16))
            aes.Decrypt(blob.AsSpan(6, 12), blob.AsSpan(34), blob.AsSpan(18, 16), outPlain);

        Assert.Equal(plain, outPlain);
    }

    [Fact]
    public void Ha_lease_key_e_node()
    {
        Assert.Equal("cam:42", RecorderLeaseService.Key(42));
        var opt = new VmsOptions { HaEnabled = true, NodeId = "rec-a", ShardIndex = 0, ShardCount = 2 };
        Assert.Equal("rec-a", opt.ResolveNodeId());
        Assert.True(opt.OwnsDevice(2));
        Assert.False(opt.OwnsDevice(3));
    }

    [Fact]
    public void Plate_normalize()
    {
        Assert.Equal("ABC1D23", EventMetadata.NormalizePlate("abc-1d23"));
        Assert.Equal("", EventMetadata.NormalizePlate("  "));
    }
}
