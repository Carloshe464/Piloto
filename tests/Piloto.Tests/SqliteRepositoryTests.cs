using Piloto.Core.Configuration;
using Piloto.Core.Models;
using Piloto.Data;
using Xunit;

namespace Piloto.Tests;

public class SqliteRepositoryTests : IDisposable
{
    private readonly string _dir;
    private readonly SqliteCallRepository _repo;

    public SqliteRepositoryTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "piloto-test-" + Guid.NewGuid().ToString("N"));
        var settings = new AppSettings { PastaDados = _dir };
        _repo = new SqliteCallRepository(settings);
        _repo.Inicializar();
    }

    private static CallRecord Registro(string texto, string? motivo = null) => new()
    {
        Metadata = new CallMetadata { Numero = "11999998888", TicketId = "T-1" },
        Transcript = TestData.Fala(texto),
        Resumo = new LlmSummary { MotivoContato = motivo },
        Duracao = TimeSpan.FromSeconds(30),
        TempoFalado = TimeSpan.FromSeconds(12),
    };

    [Fact]
    public void SalvarEListarFazRoundTrip()
    {
        var id = _repo.SalvarRegistro(Registro("cliente quer segunda via do boleto", "Segunda via de boleto"));
        Assert.True(id > 0);

        var lista = _repo.ListarRegistros();
        Assert.Single(lista);
        Assert.Equal("Segunda via de boleto", lista[0].Resumo.MotivoContato);
        Assert.False(lista[0].Transcript.EstaVazio);
    }

    [Fact]
    public void ContatoDoCadastroSobreviveAoRoundTrip()
    {
        // Colunas da migração v2. Sem elas, e-mail/telefone lidos do Zendesk se perdiam
        // ao reabrir o registro e a origem do valor virava um mistério.
        var registro = Registro("cliente confirmou os dados");
        registro.Metadata.EmailCliente = "maria@empresa.com";
        registro.Metadata.TelefoneCliente = "11912345678";
        registro.Metadata.NomeCliente = "Maria Souza";

        var id = _repo.SalvarRegistro(registro);
        var lido = _repo.ObterRegistro(id);

        Assert.NotNull(lido);
        Assert.Equal("maria@empresa.com", lido!.Metadata.EmailCliente);
        Assert.Equal("11912345678", lido.Metadata.TelefoneCliente);
        Assert.Equal("Maria Souza", lido.Metadata.NomeCliente);
    }

    [Fact]
    public void OrigemDoCampoSobreviveAoRoundTrip()
    {
        var registro = Registro("cliente confirmou os dados");
        registro.Campos.Emails.Add(new ExtractedValue
        {
            Tipo = FieldType.Email, Valor = "maria@empresa.com", TrechoOrigem = "cadastro do Zendesk",
            Confianca = 1.0, Origem = FieldSource.Extensao,
        });

        var lido = _repo.ObterRegistro(_repo.SalvarRegistro(registro));

        Assert.NotNull(lido);
        Assert.Equal(FieldSource.Extensao, Assert.Single(lido!.Campos.Emails).Origem);
    }

    [Fact]
    public void ObterPorUuidEncontraORegistroDoReprocessamento()
    {
        // O resultado do reprocessamento volta com o mesmo call_id. É por este caminho
        // que ele atualiza a ligação existente em vez de duplicá-la na lista.
        var registro = Registro("cliente quer segunda via");
        registro.Uuid = "0b2495ccc52d4a44bae8a4a863b67fc4";
        var id = _repo.SalvarRegistro(registro);

        var achado = _repo.ObterPorUuid("0b2495ccc52d4a44bae8a4a863b67fc4");

        Assert.NotNull(achado);
        Assert.Equal(id, achado!.Id);
        Assert.Null(_repo.ObterPorUuid("nao-existe"));
    }

    [Fact]
    public void BuscaFtsEncontraPorTermo()
    {
        _repo.SalvarRegistro(Registro("cliente quer segunda via do boleto"));
        _repo.SalvarRegistro(Registro("reclamação sobre atraso na entrega"));

        var achados = _repo.Buscar("boleto");
        Assert.Single(achados);

        var achados2 = _repo.Buscar("entrega");
        Assert.Single(achados2);
    }

    [Fact]
    public void ContadoresRefletemRegistros()
    {
        _repo.SalvarRegistro(Registro("um"));
        _repo.SalvarRegistro(Registro("dois"));

        var c = _repo.Contadores();
        Assert.Equal(2, c.TotalChamadas);
        Assert.True(c.TempoTotalFalado > TimeSpan.Zero);
    }

    [Fact]
    public void FilaProcessaUmPorVez()
    {
        var item = new QueueItem
        {
            CaminhoAudioAtendente = "a.wav",
            CaminhoAudioCliente = "c.wav",
        };
        var id = _repo.EnfileirarItem(item);
        Assert.True(id > 0);

        var pendente = _repo.ProximoPendente();
        Assert.NotNull(pendente);
        Assert.Equal(id, pendente!.Id);

        pendente.Estado = QueueState.Concluido;
        _repo.AtualizarItem(pendente);

        Assert.Null(_repo.ProximoPendente());
        Assert.Equal(0, _repo.ContarPendentes());
    }

    [Fact]
    public void RecuperaItensOrfaosDeExecucaoAnterior()
    {
        var item = new QueueItem
        {
            CaminhoAudioAtendente = "a.wav",
            CaminhoAudioCliente = "c.wav",
        };
        _repo.EnfileirarItem(item);

        // Simula crash no meio do processamento: item fica preso em Processando.
        item.Estado = QueueState.Processando;
        _repo.AtualizarItem(item);
        Assert.Null(_repo.ProximoPendente());

        var recuperados = _repo.RecuperarItensOrfaos(3);
        Assert.Equal(1, recuperados);

        var pendente = _repo.ProximoPendente();
        Assert.NotNull(pendente);
        Assert.Equal(item.Id, pendente!.Id);
        Assert.Equal(1, pendente.Tentativas); // a queda conta como tentativa

        // Itens concluídos não são tocados.
        pendente.Estado = QueueState.Concluido;
        _repo.AtualizarItem(pendente);
        Assert.Equal(0, _repo.RecuperarItensOrfaos(3));
    }

    [Fact]
    public void ItemQueDerrubaOProcessoVaiParaErroAposMaxQuedas()
    {
        var item = new QueueItem
        {
            CaminhoAudioAtendente = "a.wav",
            CaminhoAudioCliente = "c.wav",
        };
        _repo.EnfileirarItem(item);

        // Um item cujo processamento derruba o processo (crash nativo) cai em
        // Processando a cada boot; sem limite, seria um loop infinito de crash.
        for (var queda = 1; queda <= 3; queda++)
        {
            var preso = queda == 1 ? item : _repo.ProximoPendente();
            Assert.NotNull(preso);
            preso!.Estado = QueueState.Processando;
            _repo.AtualizarItem(preso);

            Assert.Equal(1, _repo.RecuperarItensOrfaos(3));
        }

        // Após a 3ª queda o item fica em Erro e não volta mais para a fila.
        Assert.Null(_repo.ProximoPendente());
    }

    [Fact]
    public void RetencaoRecenteNaoRemoveNada()
    {
        _repo.SalvarRegistro(Registro("recente"));
        var r = _repo.AplicarRetencao(9999, 9999);
        Assert.Equal(0, r.AudiosRemovidos);
        Assert.Equal(0, r.RegistrosRemovidos);
    }

    public void Dispose()
    {
        _repo.Dispose();
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch { /* WAL pode segurar arquivos brevemente */ }
    }
}
