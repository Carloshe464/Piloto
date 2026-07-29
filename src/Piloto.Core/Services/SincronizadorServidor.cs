using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Piloto.Core.Abstractions;
using Piloto.Core.Configuration;
using Piloto.Core.Models;

namespace Piloto.Core.Services;

/// <summary>
/// Acompanha as ligações que estão sendo processadas no servidor e grava o resultado no
/// banco local assim que fica pronto.
/// <para>
/// É o que faz a tela continuar igual: o histórico, a busca e a janela de detalhe seguem
/// lendo <c>CallRecord</c> do SQLite, sem saber que a transcrição passou a acontecer
/// noutra máquina. O banco local virou um espelho do servidor em vez da origem do dado.
/// </para>
/// <para>
/// A espera vive em disco (<c>%LOCALAPPDATA%\Piloto\aguardando</c>), não em memória: o
/// atendente fecha o app, desliga a máquina, e a ligação continua sendo esperada na
/// próxima abertura. Perder o resultado de uma ligação já enviada seria pior que não
/// tê-la enviado — o áudio já saiu daqui.
/// </para>
/// </summary>
public sealed class SincronizadorServidor : IDisposable
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ClickWriteUploader _uploader;
    private readonly ICallRepository _repo;
    private readonly ILogger<SincronizadorServidor> _log;
    private readonly string _aguardando;
    private readonly SemaphoreSlim _umDeCadaVez = new(1, 1);
    private readonly Timer _timer;

    /// <summary>Resultado gravado no banco. A tela usa para recarregar a lista.</summary>
    public event EventHandler<CallRecord>? RegistroPronto;

    /// <summary>O servidor não conseguiu processar. O argumento traz o motivo.</summary>
    public event EventHandler<string>? ProcessamentoFalhou;

    public SincronizadorServidor(
        AppSettings settings,
        ClickWriteUploader uploader,
        ICallRepository repo,
        ILogger<SincronizadorServidor> log)
    {
        _uploader = uploader;
        _repo = repo;
        _log = log;
        _aguardando = Path.Combine(settings.PastaDadosExpandida, "aguardando");
        Directory.CreateDirectory(_aguardando);

        // Intervalo curto: uma ligação de 5 minutos fica pronta em cerca de 2 no servidor,
        // e o atendente já está esperando o registro aparecer na lista.
        var intervalo = TimeSpan.FromSeconds(Math.Max(3, settings.Servidor.IntervaloConsultaSegundos));
        // Exceção escapando do callback do Timer (async void) derruba o processo e o
        // acompanhamento morre em silêncio. Fica contida e registrada.
        _timer = new Timer(async _ =>
        {
            try { await VerificarAsync().ConfigureAwait(false); }
            catch (Exception e) { _log.LogError(e, "Falha no ciclo de consulta ao servidor"); }
        }, null, intervalo, intervalo);
    }

    /// <summary>Passa a acompanhar uma ligação aceita pelo servidor.</summary>
    public void Acompanhar(string callId, CallMetadata metadata,
                           string? audioAtendente, string? audioCliente)
    {
        var espera = new Espera
        {
            CallId = callId,
            Metadata = metadata,
            AudioAtendente = audioAtendente,
            AudioCliente = audioCliente,
            EnviadaEm = DateTimeOffset.Now,
        };
        File.WriteAllText(CaminhoDe(callId), JsonSerializer.Serialize(espera, Json), Encoding.UTF8);
        _log.LogInformation("Acompanhando {CallId} no servidor", callId);
    }

    public int AguardandoResultado() =>
        Directory.Exists(_aguardando) ? Directory.EnumerateFiles(_aguardando, "*.json").Count() : 0;

    /// <summary>Consulta o servidor sobre tudo que está pendente. Chamado por timer.</summary>
    public async Task VerificarAsync(CancellationToken ct = default)
    {
        if (!await _umDeCadaVez.WaitAsync(0, ct).ConfigureAwait(false))
            return;

        try
        {
            foreach (var arquivo in Directory.EnumerateFiles(_aguardando, "*.json"))
            {
                ct.ThrowIfCancellationRequested();

                Espera? espera;
                try
                {
                    espera = JsonSerializer.Deserialize<Espera>(File.ReadAllText(arquivo), Json);
                }
                catch (JsonException)
                {
                    // Arquivo corrompido (queda no meio da escrita). Não trava a fila.
                    _log.LogWarning("Espera ilegível descartada: {Arquivo}", arquivo);
                    File.Delete(arquivo);
                    continue;
                }
                if (espera is null) { File.Delete(arquivo); continue; }

                EstadoLigacao? estado;
                try
                {
                    estado = await _uploader.ConsultarAsync(espera.CallId, ct).ConfigureAwait(false);
                }
                catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
                {
                    // Servidor fora do ar: tenta de novo no próximo ciclo, sem perder nada.
                    break;
                }

                if (estado is null)
                {
                    // 404: o servidor não conhece esta ligação. Aconteceu de verdade quando
                    // o banco do servidor foi recriado — insistir para sempre não ajuda.
                    _log.LogWarning("Servidor não conhece {CallId} — deixando de acompanhar", espera.CallId);
                    File.Delete(arquivo);
                    continue;
                }

                if (estado.EmAndamento)
                    continue;

                if (estado.Falhou)
                {
                    _log.LogError("Servidor falhou em {CallId}: {Erro}", espera.CallId, estado.Erro);
                    ProcessamentoFalhou?.Invoke(this, estado.Erro ?? "erro no servidor");
                    File.Delete(arquivo);
                    continue;
                }

                if (estado.Resultado is null)
                {
                    File.Delete(arquivo);
                    continue;
                }

                Gravar(estado.Resultado, espera);
                File.Delete(arquivo);
            }
        }
        finally
        {
            _umDeCadaVez.Release();
        }
    }

    private void Gravar(ResultadoServidor resultado, Espera espera)
    {
        var registro = MapeadorResultado.ParaRegistro(
            resultado, espera.Metadata, espera.AudioAtendente, espera.AudioCliente);

        // Reprocessamento devolve o MESMO call_id: o registro já existe e é atualizado no
        // lugar. Inserir de novo criaria a mesma ligação duas vezes na lista — e o
        // atendente ficaria sem saber qual das duas é a versão nova.
        var existente = _repo.ObterPorUuid(registro.Uuid);
        if (existente is not null)
        {
            registro.Id = existente.Id;
            registro.CriadoEm = existente.CriadoEm;
            // O áudio da ligação original continua valendo: o reprocesso não reenvia áudio.
            registro.CaminhoAudioAtendente ??= existente.CaminhoAudioAtendente;
            registro.CaminhoAudioCliente ??= existente.CaminhoAudioCliente;

            _repo.AtualizarRegistro(registro);
            _log.LogInformation(
                "Registro {Id} atualizado pelo reprocessamento no servidor ({Turnos} turnos, revisão={Revisao})",
                registro.Id, registro.Transcript.Segmentos.Count, registro.PrecisaRevisao);
        }
        else
        {
            registro.Id = _repo.SalvarRegistro(registro);
            _log.LogInformation(
                "Registro {Id} gravado a partir do servidor ({Turnos} turnos, revisão={Revisao})",
                registro.Id, registro.Transcript.Segmentos.Count, registro.PrecisaRevisao);
        }

        RegistroPronto?.Invoke(this, registro);
    }

    private string CaminhoDe(string callId) => Path.Combine(_aguardando, $"{callId}.json");

    public void Dispose()
    {
        _timer.Dispose();
        _umDeCadaVez.Dispose();
    }

    /// <summary>O que precisa sobreviver ao fechamento do app para o resultado não se perder.</summary>
    private sealed record Espera
    {
        public string CallId { get; init; } = "";
        public CallMetadata Metadata { get; init; } = CallMetadata.Vazio();
        public string? AudioAtendente { get; init; }
        public string? AudioCliente { get; init; }
        public DateTimeOffset EnviadaEm { get; init; }
    }
}
