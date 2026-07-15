using Microsoft.Extensions.Logging;
using Piloto.Bridge;
using Piloto.Core.Abstractions;
using Piloto.Core.Models;
using Piloto.Core.Services;

namespace Piloto.App.Services;

/// <summary>
/// Orquestra a gravação a partir da UI: mantém os metadados correntes (atualizados pela
/// extensão via bridge), inicia/para o gravador e enfileira o par de áudios ao encerrar.
/// No MVP o start/stop é manual; a bridge apenas alimenta os metadados.
/// </summary>
public sealed class RecordingCoordinator
{
    private readonly IAudioRecorder _recorder;
    private readonly CallEnqueuer _enqueuer;
    private readonly ZendeskBridgeServer _bridge;
    private readonly ILogger<RecordingCoordinator> _log;
    private readonly object _lock = new();

    private CallMetadata _metadataCorrente = CallMetadata.Vazio();

    public bool EstaGravando => _recorder.EstaGravando;
    public CallMetadata MetadataCorrente { get { lock (_lock) return _metadataCorrente; } }

    public event EventHandler<bool>? EstadoGravacaoMudou;
    public event EventHandler? MetadataMudou;

    public RecordingCoordinator(
        IAudioRecorder recorder,
        CallEnqueuer enqueuer,
        ZendeskBridgeServer bridge,
        ILogger<RecordingCoordinator> log)
    {
        _recorder = recorder;
        _enqueuer = enqueuer;
        _bridge = bridge;
        _log = log;

        _recorder.EstadoGravacaoMudou += (_, gravando) => EstadoGravacaoMudou?.Invoke(this, gravando);
        _bridge.MetadataAtualizada += (_, meta) => AtualizarMetadata(meta);
        _bridge.ChamadaIniciada += (_, meta) => AtualizarMetadata(meta);
        _bridge.ChamadaEncerrada += (_, meta) => AtualizarMetadata(meta);
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
