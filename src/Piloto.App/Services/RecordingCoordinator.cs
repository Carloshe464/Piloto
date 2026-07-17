using Microsoft.Extensions.Logging;
using Piloto.Audio;
using Piloto.Bridge;
using Piloto.Core.Abstractions;
using Piloto.Core.Models;
using Piloto.Core.Services;

namespace Piloto.App.Services;

/// <summary>
/// Orquestra a gravação: mantém os metadados correntes (via bridge), controla o gravador
/// WASAPI manual (botão) e a captura automática pela extensão (hook WebRTC) — esta começa
/// e termina sozinha nas fronteiras reais da chamada e enfileira ao encerrar.
/// </summary>
public sealed class RecordingCoordinator
{
    private readonly IAudioRecorder _recorder;
    private readonly ExtensionAudioRecorder _extensao;
    private readonly CallEnqueuer _enqueuer;
    private readonly ZendeskBridgeServer _bridge;
    private readonly ILogger<RecordingCoordinator> _log;
    private readonly object _lock = new();

    private CallMetadata _metadataCorrente = CallMetadata.Vazio();

    /// <summary>Estado do gravador manual (botão da UI); a sessão da extensão é
    /// autônoma e sinalizada apenas via <see cref="EstadoGravacaoMudou"/>.</summary>
    public bool EstaGravando => _recorder.EstaGravando;
    public CallMetadata MetadataCorrente { get { lock (_lock) return _metadataCorrente; } }

    public event EventHandler<bool>? EstadoGravacaoMudou;
    public event EventHandler<string>? AvisoCaptura;
    public event EventHandler? MetadataMudou;
    public event EventHandler<long>? ChamadaEnfileirada;

    public RecordingCoordinator(
        IAudioRecorder recorder,
        ExtensionAudioRecorder extensao,
        CallEnqueuer enqueuer,
        ZendeskBridgeServer bridge,
        ILogger<RecordingCoordinator> log)
    {
        _recorder = recorder;
        _extensao = extensao;
        _enqueuer = enqueuer;
        _bridge = bridge;
        _log = log;

        _recorder.EstadoGravacaoMudou += (_, gravando) => EstadoGravacaoMudou?.Invoke(this, gravando);
        _recorder.AvisoCaptura += (_, msg) => AvisoCaptura?.Invoke(this, msg);
        _bridge.MetadataAtualizada += (_, meta) => AtualizarMetadata(meta);
        _bridge.ChamadaIniciada += (_, meta) => AtualizarMetadata(meta);
        _bridge.ChamadaEncerrada += (_, meta) => AtualizarMetadata(meta);

        _bridge.AudioIniciado += (_, taxa) => IniciarSessaoExtensao(taxa);
        _bridge.AudioChunkRecebido += (_, e) => _extensao.ReceberChunk(e.Canal, e.Dados);
        _bridge.AudioEncerrado += (_, _) => EncerrarSessaoExtensao();
    }

    private void IniciarSessaoExtensao(int taxa)
    {
        if (_recorder.EstaGravando)
        {
            // Gravação manual em andamento: não duplica a mesma chamada.
            _log.LogWarning("Extensão iniciou áudio com gravação manual ativa — sessão da extensão ignorada");
            return;
        }

        // Sessão anterior sem "fim" (aba fechada, navegador caiu): fecha e aproveita o que há.
        if (_extensao.Ativa)
            EncerrarSessaoExtensao();

        CallMetadata snapshot;
        lock (_lock)
        {
            snapshot = new CallMetadata
            {
                Numero = _metadataCorrente.Numero,
                TicketId = _metadataCorrente.TicketId,
                Status = _metadataCorrente.Status,
                Atendente = _metadataCorrente.Atendente,
            };
        }

        _extensao.Iniciar(snapshot, taxa);
        EstadoGravacaoMudou?.Invoke(this, true);
    }

    private void EncerrarSessaoExtensao()
    {
        if (!_extensao.Ativa) return;

        var captura = _extensao.Encerrar();
        EstadoGravacaoMudou?.Invoke(this, false);
        if (captura is null) return;

        var id = _enqueuer.Enfileirar(captura);
        _log.LogInformation("Chamada enfileirada (item {Id}) — áudio capturado pela extensão", id);
        ChamadaEnfileirada?.Invoke(this, id);
        LimparMetadata();
    }

    private void AtualizarMetadata(CallMetadata meta)
    {
        lock (_lock)
        {
            // Preserva campos já conhecidos quando a nova mensagem vier parcial.
            _metadataCorrente = new CallMetadata
            {
                Numero = meta.Numero ?? _metadataCorrente.Numero,
                TicketId = meta.TicketId ?? _metadataCorrente.TicketId,
                Status = meta.Status ?? _metadataCorrente.Status,
                Atendente = meta.Atendente ?? _metadataCorrente.Atendente,
            };
        }
        MetadataMudou?.Invoke(this, EventArgs.Empty);
    }

    public void Iniciar()
    {
        CallMetadata snapshot;
        lock (_lock)
        {
            snapshot = new CallMetadata
            {
                Numero = _metadataCorrente.Numero,
                TicketId = _metadataCorrente.TicketId,
                Status = _metadataCorrente.Status,
                Atendente = _metadataCorrente.Atendente,
                IniciadaEm = DateTimeOffset.Now,
            };
        }
        _recorder.Iniciar(snapshot);
    }

    /// <summary>Para a gravação e enfileira. Retorna o id do item na fila.</summary>
    public long PararEEnfileirar()
    {
        var captura = _recorder.Parar();
        var id = _enqueuer.Enfileirar(captura);
        _log.LogInformation("Chamada enfileirada (item {Id})", id);
        LimparMetadata();
        return id;
    }

    public void Descartar()
    {
        _recorder.Descartar();
        LimparMetadata();
    }

    private void LimparMetadata()
    {
        lock (_lock) _metadataCorrente = CallMetadata.Vazio();
        MetadataMudou?.Invoke(this, EventArgs.Empty);
    }
}
