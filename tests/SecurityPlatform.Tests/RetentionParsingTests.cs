using SecurityPlatform.Modules.Vms;

namespace SecurityPlatform.Tests;

/// <summary>
/// O inicio da gravacao vem do nome do arquivo. Se a leitura errar, a linha do
/// tempo do playback desalinha e o export recorta o trecho errado.
/// </summary>
public class RetentionParsingTests
{
    [Theory]
    [InlineData("c_20260722_143000", 2026, 7, 22, 14, 30, 0)]
    [InlineData("e_20250101_000000", 2025, 1, 1, 0, 0, 0)]
    [InlineData("20241231_235959", 2024, 12, 31, 23, 59, 59)]   // formato antigo, sem prefixo
    public void Le_o_instante_do_nome_do_arquivo(
        string nome, int ano, int mes, int dia, int hora, int min, int seg)
    {
        var quando = RetentionService.ParseStart(nome);

        Assert.NotNull(quando);
        Assert.Equal(new DateTime(ano, mes, dia, hora, min, seg, DateTimeKind.Utc), quando!.Value);
        Assert.Equal(DateTimeKind.Utc, quando.Value.Kind);
    }

    [Theory]
    [InlineData("gravacao")]
    [InlineData("c_2026-07-22_14-30-00")]
    [InlineData("")]
    public void Nome_fora_do_padrao_devolve_nulo_para_o_chamador_cair_no_fallback(string nome)
        => Assert.Null(RetentionService.ParseStart(nome));
}

/// <summary>
/// O confinamento de caminho e o que impede baixar arquivo de fora da raiz de
/// storage por um registro adulterado no banco.
/// </summary>
public class PathConfinementTests
{
    [Fact]
    public void Aceita_arquivo_dentro_da_raiz()
        => Assert.True(VmsEndpoints.IsInside(
            Path.Combine("C:", "data", "recordings", "1", "c_20260722_143000.mp4"),
            Path.Combine("C:", "data", "recordings")));

    [Fact]
    public void Recusa_prefixo_parecido_que_nao_e_subpasta()
        => Assert.False(VmsEndpoints.IsInside(
            Path.Combine("C:", "data", "recordings-antigo", "x.mp4"),
            Path.Combine("C:", "data", "recordings")));

    [Fact]
    public void Recusa_travessia_para_fora_da_raiz()
        => Assert.False(VmsEndpoints.IsInside(
            Path.Combine("C:", "data", "recordings", "..", "..", "windows", "system32", "config"),
            Path.Combine("C:", "data", "recordings")));
}

/// <summary>
/// Sharding: cada camera precisa pertencer a exatamente um no. Um furo aqui
/// duplica evento no banco ou deixa camera sem gravar.
/// </summary>
public class ShardingTests
{
    [Fact]
    public void No_unico_assume_todas_as_cameras()
    {
        var opt = new VmsOptions { ShardCount = 1, ShardIndex = 0 };
        Assert.All(Enumerable.Range(1, 50), id => Assert.True(opt.OwnsDevice(id)));
    }

    [Fact]
    public void Cada_camera_pertence_a_exatamente_um_no()
    {
        const int nos = 4;
        var instancias = Enumerable.Range(0, nos)
            .Select(i => new VmsOptions { ShardCount = nos, ShardIndex = i })
            .ToList();

        foreach (var deviceId in Enumerable.Range(1, 200))
            Assert.Equal(1, instancias.Count(o => o.OwnsDevice(deviceId)));
    }
}
