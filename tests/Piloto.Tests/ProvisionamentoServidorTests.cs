using Piloto.Core.Configuration;
using Xunit;

namespace Piloto.Tests;

/// <summary>
/// Provisionamento do servidor pelo instalador.
/// <para>
/// A regra que estes testes protegem é a que decide quem ganha quando as duas fontes
/// discordam: o arquivo deixado pelo instalador ou o que o atendente digitou na tela.
/// Errar para um lado desfaz a configuração do atendente a cada abertura; errar para o
/// outro faz uma reinstalação para trocar de servidor não ter efeito nenhum.
/// </para>
/// </summary>
public class ProvisionamentoServidorTests : IDisposable
{
    private readonly string _pasta = Path.Combine(Path.GetTempPath(), "cw-prov-" + Guid.NewGuid().ToString("N")[..8]);

    public ProvisionamentoServidorTests() => Directory.CreateDirectory(_pasta);

    public void Dispose()
    {
        try { Directory.Delete(_pasta, recursive: true); } catch { /* temp */ }
    }

    private string Escrever(string conteudo, DateTime? quando = null)
    {
        var caminho = Path.Combine(_pasta, "servidor.json");
        File.WriteAllText(caminho, conteudo);
        if (quando is { } t)
            File.SetLastWriteTimeUtc(caminho, t);
        return caminho;
    }

    private static string Json(string url, string token) =>
        $$"""{ "url": "{{url}}", "token": "{{token}}" }""";

    [Fact]
    public void Aplica_endereco_e_token_na_primeira_abertura()
    {
        var arquivo = Escrever(Json("http://192.168.0.10:8517", "tok-abc"));
        var settings = new AppSettings();

        Assert.True(ProvisionamentoServidor.Aplicar(settings, arquivo));
        Assert.Equal("http://192.168.0.10:8517", settings.Servidor.Url);
        Assert.Equal("tok-abc", settings.Servidor.Token);
        Assert.NotNull(settings.Servidor.ProvisionadoEm);
    }

    [Fact]
    public void Nao_reaplica_na_abertura_seguinte()
    {
        var arquivo = Escrever(Json("http://servidor:8517", "tok-abc"));
        var settings = new AppSettings();
        ProvisionamentoServidor.Aplicar(settings, arquivo);

        Assert.False(ProvisionamentoServidor.Aplicar(settings, arquivo));
    }

    [Fact]
    public void Edicao_do_atendente_sobrevive_as_aberturas()
    {
        // É o caso que mais importa: o token foi trocado na tela e não pode voltar
        // ao valor do instalador toda vez que o app abre.
        var arquivo = Escrever(Json("http://servidor:8517", "tok-do-instalador"));
        var settings = new AppSettings();
        ProvisionamentoServidor.Aplicar(settings, arquivo);

        settings.Servidor.Token = "tok-corrigido-na-tela";
        settings.Servidor.ProvisionadoEm = DateTimeOffset.UtcNow;

        Assert.False(ProvisionamentoServidor.Aplicar(settings, arquivo));
        Assert.Equal("tok-corrigido-na-tela", settings.Servidor.Token);
    }

    [Fact]
    public void Reinstalacao_para_trocar_de_servidor_tem_efeito()
    {
        var arquivo = Escrever(Json("http://antigo:8517", "tok-antigo"),
                               DateTime.UtcNow.AddDays(-2));
        var settings = new AppSettings();
        ProvisionamentoServidor.Aplicar(settings, arquivo);

        // Reinstalar grava o arquivo de novo, com data nova.
        Escrever(Json("http://novo:8517", "tok-novo"), DateTime.UtcNow);

        Assert.True(ProvisionamentoServidor.Aplicar(settings, arquivo));
        Assert.Equal("http://novo:8517", settings.Servidor.Url);
        Assert.Equal("tok-novo", settings.Servidor.Token);
    }

    [Fact]
    public void Campo_vazio_no_arquivo_nao_apaga_o_que_ja_existe()
    {
        // Instalação silenciosa sem /TOKEN= grava token vazio; isso não pode zerar
        // um token que já estava funcionando.
        var settings = new AppSettings();
        settings.Servidor.Token = "tok-existente";
        var arquivo = Escrever(Json("http://novo:8517", ""));

        ProvisionamentoServidor.Aplicar(settings, arquivo);

        Assert.Equal("http://novo:8517", settings.Servidor.Url);
        Assert.Equal("tok-existente", settings.Servidor.Token);
    }

    [Fact]
    public void Arquivo_ausente_nao_faz_nada()
    {
        var settings = new AppSettings();
        Assert.False(ProvisionamentoServidor.Aplicar(settings, Path.Combine(_pasta, "nao-existe.json")));
    }

    [Fact]
    public void Arquivo_corrompido_nao_impede_o_app_de_abrir()
    {
        var arquivo = Escrever("{ isto nao e json");
        var settings = new AppSettings();
        settings.Servidor.Url = "http://que-funcionava:8517";

        Assert.False(ProvisionamentoServidor.Aplicar(settings, arquivo));
        Assert.Equal("http://que-funcionava:8517", settings.Servidor.Url);
    }

    [Fact]
    public void Espacos_em_volta_dos_valores_sao_descartados()
    {
        // Colar o token no assistente costuma trazer espaço junto; com ele o servidor
        // recusaria a autenticação e o motivo seria invisível.
        var arquivo = Escrever(Json("  http://servidor:8517  ", "  tok-abc  "));
        var settings = new AppSettings();

        ProvisionamentoServidor.Aplicar(settings, arquivo);

        Assert.Equal("http://servidor:8517", settings.Servidor.Url);
        Assert.Equal("tok-abc", settings.Servidor.Token);
    }
}
