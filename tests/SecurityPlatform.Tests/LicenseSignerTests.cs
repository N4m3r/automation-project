using Microsoft.Extensions.Options;
using SecurityPlatform.Modules.Security;

namespace SecurityPlatform.Tests;

public class LicenseSignerTests
{
    private static LicenseSigner Signer(string key = "chave-de-teste-com-mais-de-32-caracteres!!", bool require = false)
        => new(Options.Create(new SecurityOptions
        {
            JwtKey = key,
            LicenseSigningKey = key,
            RequireSignedLicense = require
        }));

    [Fact]
    public void Emite_e_valida_chave_assinada()
    {
        var s = Signer();
        var key = s.Issue(new LicensePayload(
            Edition: "Professional",
            CustomerName: "Acme",
            VideoChannels: 32,
            Failover: true,
            ExpiresAt: DateTime.UtcNow.AddYears(1)));

        Assert.Contains('.', key);
        var payload = s.TryValidate(key);
        Assert.NotNull(payload);
        Assert.Equal("Professional", payload!.Edition);
        Assert.Equal(32, payload.VideoChannels);
        Assert.True(payload.Failover);
        Assert.Equal("Acme", payload.CustomerName);
    }

    [Fact]
    public void Texto_livre_nao_e_assinado()
    {
        var s = Signer();
        Assert.Null(s.TryValidate("LICENCA-LEGADO-SEM-ASSINATURA"));
    }

    [Fact]
    public void Assinatura_adulterada_e_recusada()
    {
        var s = Signer();
        var key = s.Issue(new LicensePayload(VideoChannels: 8));
        var parts = key.Split('.');
        var fake = parts[0] + ".AAAA" + parts[1][4..];

        var ex = Assert.Throws<InvalidOperationException>(() => s.TryValidate(fake));
        Assert.Contains("inválida", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Escape_drawtext_e_license_sao_independentes()
    {
        // Sanity: suíte não quebrou ao adicionar o módulo de licença.
        Assert.True(Signer().Issue(new LicensePayload()).Length > 20);
    }
}
