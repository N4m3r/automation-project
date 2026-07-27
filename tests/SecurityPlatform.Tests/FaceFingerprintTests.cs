using SecurityPlatform.Modules.Vms;

namespace SecurityPlatform.Tests;

public class FaceFingerprintTests
{
    private static byte[] SolidGray(byte value)
    {
        var g = new byte[FaceFingerprint.Side * FaceFingerprint.Side];
        Array.Fill(g, value);
        return g;
    }

    private static byte[] GradientHorizontal()
    {
        var g = new byte[FaceFingerprint.Side * FaceFingerprint.Side];
        for (var y = 0; y < FaceFingerprint.Side; y++)
        for (var x = 0; x < FaceFingerprint.Side; x++)
            g[y * FaceFingerprint.Side + x] = (byte)(x * 255 / (FaceFingerprint.Side - 1));
        return g;
    }

    private static byte[] GradientVertical()
    {
        var g = new byte[FaceFingerprint.Side * FaceFingerprint.Side];
        for (var y = 0; y < FaceFingerprint.Side; y++)
        for (var x = 0; x < FaceFingerprint.Side; x++)
            g[y * FaceFingerprint.Side + x] = (byte)(y * 255 / (FaceFingerprint.Side - 1));
        return g;
    }

    [Fact]
    public void Mesma_imagem_tem_similaridade_alta()
    {
        var a = FaceFingerprint.FromGray64(GradientHorizontal());
        var b = FaceFingerprint.FromGray64(GradientHorizontal());
        var score = FaceFingerprint.Similarity(a, b);
        Assert.True(score > 0.98f, $"score={score}");
    }

    [Fact]
    public void Imagens_diferentes_tem_score_menor()
    {
        var a = FaceFingerprint.FromGray64(GradientHorizontal());
        var b = FaceFingerprint.FromGray64(GradientVertical());
        var score = FaceFingerprint.Similarity(a, b);
        Assert.True(score < 0.98f, $"score={score}");
    }

    [Fact]
    public void Encode_decode_preserva_vetor()
    {
        var v = FaceFingerprint.FromGray64(SolidGray(128));
        var enc = FaceFingerprint.Encode(v);
        var dec = FaceFingerprint.Decode(enc);
        Assert.NotNull(dec);
        Assert.Equal(v.Length, dec!.Length);
        for (var i = 0; i < v.Length; i++)
            Assert.Equal(v[i], dec[i], precision: 5);
    }

    [Fact]
    public void Decode_json_array_compativel()
    {
        var dec = FaceFingerprint.Decode("[0.1, 0.2, 0.3, 0.4]");
        Assert.NotNull(dec);
        Assert.Equal(4, dec!.Length);
        Assert.Equal(0.1f, dec[0], precision: 5);
    }

    [Fact]
    public void DecodeImagePayload_data_url()
    {
        var raw = Convert.ToBase64String(new byte[] { 1, 2, 3, 4 });
        var bytes = FaceFingerprint.DecodeImagePayload("data:image/jpeg;base64," + raw);
        Assert.NotNull(bytes);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, bytes);
    }

    [Fact]
    public void Feature_tem_dimensao_fixada()
    {
        var v = FaceFingerprint.FromGray64(SolidGray(40));
        Assert.Equal(FaceFingerprint.FeatureLen, v.Length);
    }
}
