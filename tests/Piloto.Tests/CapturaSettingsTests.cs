using Piloto.Core.Configuration;
using Xunit;

namespace Piloto.Tests;

/// <summary>
/// Espera pelo ticket antes de enviar a gravação.
/// <para>
/// O prazo é lido do <c>appsettings.json</c> e precisa sobreviver a duas situações que
/// acontecem em campo: instalação antiga, cujo arquivo não tem a seção nova, e
/// administrador que zera o valor para voltar ao envio imediato. Nos dois casos o app não
/// pode subir com prazo indefinido nem quebrar na leitura.
/// </para>
/// </summary>
public class CapturaSettingsTests : IDisposable
{
    private readonly string _pasta =
        Path.Combine(Path.GetTempPath(), "cw-capt-" + Guid.NewGuid().ToString("N")[..8]);

    public CapturaSettingsTests() => Directory.CreateDirectory(_pasta);

    public void Dispose()
    {
        try { Directory.Delete(_pasta, recursive: true); } catch { /* temp */ }
    }

    private string Escrever(string conteudo)
    {
        var caminho = Path.Combine(_pasta, "appsettings.json");
        File.WriteAllText(caminho, conteudo);
        return caminho;
    }

    [Fact]
    public void Prazo_padrao_cobre_a_abertura_do_ticket()
    {
        // O ticket abre cerca de 10 s depois de a ligação cair; o padrão precisa de folga
        // sobre isso, senão a espera vence justamente antes do dado chegar.
        Assert.True(new AppSettings().Captura.EsperaIdentificacaoSegundos >= 10);
    }

    [Fact]
    public void Arquivo_sem_a_secao_captura_mantem_o_padrao()
    {
        // Instalação anterior à 1.2: o appsettings.json em %LOCALAPPDATA% não tem a seção.
        var arquivo = Escrever("""{ "bridge": { "porta": 8517 } }""");

        var settings = AppSettings.Load(arquivo);

        Assert.Equal(new AppSettings().Captura.EsperaIdentificacaoSegundos,
                     settings.Captura.EsperaIdentificacaoSegundos);
    }

    [Fact]
    public void Prazo_do_arquivo_vence_o_padrao()
    {
        var arquivo = Escrever("""{ "captura": { "esperaIdentificacaoSegundos": 25 } }""");

        Assert.Equal(25, AppSettings.Load(arquivo).Captura.EsperaIdentificacaoSegundos);
    }

    [Fact]
    public void Zero_e_valor_valido_e_significa_enviar_na_hora()
    {
        var arquivo = Escrever("""{ "captura": { "esperaIdentificacaoSegundos": 0 } }""");

        Assert.Equal(0, AppSettings.Load(arquivo).Captura.EsperaIdentificacaoSegundos);
    }

    [Fact]
    public void Ida_e_volta_pelo_disco_preserva_o_prazo()
    {
        // A tela de ajustes salva o objeto inteiro; a seção nova não pode sumir no Save.
        var caminho = Path.Combine(_pasta, "roundtrip.json");
        var original = new AppSettings();
        original.Captura.EsperaIdentificacaoSegundos = 12;
        original.Save(caminho);

        Assert.Equal(12, AppSettings.Load(caminho).Captura.EsperaIdentificacaoSegundos);
    }
}
