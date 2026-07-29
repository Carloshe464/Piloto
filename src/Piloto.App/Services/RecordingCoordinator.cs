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
    private readonly ClickWriteUploader _uploader;
    private readonly SincronizadorServidor _sincronizador;
    private readonly ZendeskBridgeServer _bridge;
    private readonly ICallRepository _repo;
    private readonly ILogger<RecordingCoordinator> _log;
    private readonly object _lock = new();

    private CallMetadata _metadataCorrente = CallMetadata.Vazio();

    /// <summary>Estado do gravador manual (botão da UI); a sessão da extensão é
    /// autônoma e sinalizada apenas via <see cref="EstadoGravacaoMudou"/>.</summary>
    public bool EstaGravando => _recorder.EstaGravando || _extensao.Ativa;
    public CallMetadata MetadataCorrente { get { lock (_lock) return _metadataCorrente; } }

    public event EventHandler<bool>? EstadoGravacaoMudou;
    public event EventHandler<string>? AvisoCaptura;
    public event EventHandler? MetadataMudou;
    public event EventHandler<CallRecord>? RegistroProvisorioCriado;

    /// <summary>Servidor aceitou a ligação. O argumento traz o call_id.</summary>
    public event EventHandler<RespostaEnvio>? ChamadaEnviada;

    /// <summary>Servidor inacessível: a gravação ficou retida em disco e sobe depois.</summary>
    public event EventHandler<string>? EnvioAdiado;

    public RecordingCoordinator(
        IAudioRecorder recorder,
        ExtensionAudioRecorder extensao,
        ClickWriteUploader uploader,
        SincronizadorServidor sincronizador,
        ZendeskBridgeServer bridge,
        ICallRepository repo,
        ILogger<RecordingCoordinator> log)
    {
        _recorder = recorder;
        _extensao = extensao;
        _uploader = uploader;
        _sincronizador = sincronizador;
        _bridge = bridge;
        _repo = repo;
        _log = log;

        _recorder.EstadoGravacaoMudou += (_, gravando) => EstadoGravacaoMudou?.Invoke(this, gravando);
        _recorder.AvisoCaptura += (_, msg) => AvisoCaptura?.Invoke(this, msg);
        _uploader.EnvioAdiado += (_, pasta) => EnvioAdiado?.Invoke(this, pasta);
        _uploader.PendenteSubiu += (_, e) => AcompanharPendente(e);
        _bridge.MetadataAtualizada += (_, meta) => AtualizarMetadata(meta);
        _bridge.ChamadaIniciada += (_, meta) => AtualizarMetadata(meta);
        _bridge.ChamadaEncerrada += (_, meta) => AtualizarMetadata(meta);

        _bridge.AudioIniciado += (_, taxa) => IniciarSessaoExtensao(taxa);
        _bridge.AudioChunkRecebido += (_, e) => _extensao.ReceberChunk(e.Canal, e.Dados);
        _bridge.AudioEncerrado += (_, _) => EncerrarSessaoExtensao();
    }

    /// <summary>
    /// Uma gravação retida em disco subiu sozinha (rede voltou, ou o app reabriu). Daqui
    /// para frente o caminho é o mesmo do envio direto: passa a acompanhar o resultado e
    /// avisa a tela.
    /// <para>Sem isto, a ligação subia para o servidor e o resultado nunca era buscado —
    /// o atendente via a gravação sair da fila e nunca a via aparecer na lista.</para>
    /// </summary>
    private void AcompanharPendente(PendenteEnviada e)
    {
        try
        {
            if (e.RegistroLocalId is { } registroId && _repo.ObterRegistro(registroId) is { } provisoria)
            {
                provisoria.Resumo.Status = "Processando";
                _repo.AtualizarRegistro(provisoria);
                RegistroProvisorioCriado?.Invoke(this, provisoria);
            }
            _sincronizador.Acompanhar(
                e.Resposta.CallId, e.Metadata, e.AudioAtendente, e.AudioCliente,
                e.RegistroLocalId);
            _log.LogInformation("Ligação {CallId} enviada ao servidor — fila local (reenvio automático)",
                                e.Resposta.CallId);
            ChamadaEnviada?.Invoke(this, e.Resposta);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Ligação {CallId} subiu mas não pôde ser acompanhada",
                          e.Resposta.CallId);
            AvisoCaptura?.Invoke(this, $"Ligação enviada, mas o resultado não está sendo acompanhado: {ex.Message}");
        }
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
            snapshot = Copiar(_metadataCorrente);
        }

        _extensao.Iniciar(snapshot, taxa);
        _log.LogInformation(
            "Gravação automática iniciada pela extensão (ticket {Ticket}, telefone {Telefone})",
            snapshot.TicketId ?? "-", snapshot.TelefoneCliente ?? snapshot.Numero ?? "-");
        EstadoGravacaoMudou?.Invoke(this, true);
    }

    private void EncerrarSessaoExtensao()
    {
        if (!_extensao.Ativa) return;

        var captura = _extensao.Encerrar();
        EstadoGravacaoMudou?.Invoke(this, false);
        if (captura is null) return;

        CompletarMetadata(captura.Metadata);
        _log.LogInformation("Gravação automática encerrada ({Duracao})", captura.Duracao);
        Enviar(captura, "captura automática pela extensão");
        LimparMetadata();
    }

    /// <summary>
    /// Sobe a captura para o servidor sem bloquear quem chamou.
    /// <para>
    /// Deliberadamente sem <c>await</c>: o atendente já está atendendo a próxima ligação
    /// e não pode ficar preso num upload de dezenas de megabytes. Falha de rede não se
    /// perde — o <see cref="ClickWriteUploader"/> retém a gravação em disco e reenvia
    /// sozinho depois.
    /// </para>
    /// </summary>
    private void Enviar(AudioCapture captura, string origem)
    {
        var provisoria = MapeadorResultado.CriarProvisorio(captura);
        provisoria.Id = _repo.SalvarRegistro(provisoria);
        _log.LogInformation(
            "Registro provisório {Id} criado (ticket {Ticket}, telefone {Telefone})",
            provisoria.Id, provisoria.Metadata.TicketId ?? "-",
            provisoria.Metadata.TelefoneCliente ?? provisoria.Metadata.Numero ?? "-");
        RegistroProvisorioCriado?.Invoke(this, provisoria);

        _ = Task.Run(async () =>
        {
            try
            {
                var resposta = await _uploader.EnviarAsync(captura, provisoria.Id).ConfigureAwait(false);
                if (resposta is null)
                {
                    provisoria.Resumo.Status = "Aguardando envio";
                    _repo.AtualizarRegistro(provisoria);
                    RegistroProvisorioCriado?.Invoke(this, provisoria);
                    return;
                }

                // A espera vai para disco antes de qualquer notificação de tela: se o app
                // fechar no instante seguinte, o resultado continua sendo buscado na
                // próxima abertura. O áudio já saiu daqui — perder o retorno seria pior
                // que não ter enviado.
                _sincronizador.Acompanhar(
                    resposta.CallId, captura.Metadata,
                    captura.CaminhoAtendente, captura.CaminhoCliente, provisoria.Id);

                _log.LogInformation("Ligação {CallId} enviada ao servidor — {Origem}",
                                    resposta.CallId, origem);
                ChamadaEnviada?.Invoke(this, resposta);
            }
            catch (Exception e)
            {
                provisoria.Resumo.Status = "Falha no envio";
                provisoria.MarcarRevisao($"Falha no envio: {e.Message}");
                _repo.AtualizarRegistro(provisoria);
                RegistroProvisorioCriado?.Invoke(this, provisoria);
                _log.LogError(e, "Falha ao enviar a ligação ao servidor");
                AvisoCaptura?.Invoke(this, $"Falha ao enviar ao servidor: {e.Message}");
            }
        });
    }

    private void AtualizarMetadata(CallMetadata meta)
    {
        var mudouIdentificacao = false;
        lock (_lock)
        {
            mudouIdentificacao = (!string.IsNullOrWhiteSpace(meta.TicketId)
                                   && meta.TicketId != _metadataCorrente.TicketId)
                                  || (!string.IsNullOrWhiteSpace(meta.TelefoneCliente ?? meta.Numero)
                                      && (meta.TelefoneCliente ?? meta.Numero)
                                      != (_metadataCorrente.TelefoneCliente ?? _metadataCorrente.Numero));
            // Preserva campos já conhecidos quando a nova mensagem vier parcial.
            _metadataCorrente = new CallMetadata
            {
                Numero = meta.Numero ?? _metadataCorrente.Numero,
                TicketId = meta.TicketId ?? _metadataCorrente.TicketId,
                Status = meta.Status ?? _metadataCorrente.Status,
                Atendente = meta.Atendente ?? _metadataCorrente.Atendente,
                EmailCliente = meta.EmailCliente ?? _metadataCorrente.EmailCliente,
                TelefoneCliente = meta.TelefoneCliente ?? _metadataCorrente.TelefoneCliente,
                NomeCliente = meta.NomeCliente ?? _metadataCorrente.NomeCliente,
                // A sessão da extensão começa no evento call_started. Não perder o
                // carimbo ao chegar um metadata parcial depois é essencial para o
                // tempo da ligação refletir o começo real do softphone.
                IniciadaEm = meta.IniciadaEm ?? _metadataCorrente.IniciadaEm,
                EncerradaEm = meta.EncerradaEm ?? _metadataCorrente.EncerradaEm,
            };
        }
        if (mudouIdentificacao)
            _log.LogInformation("Metadados recebidos da extensão (ticket {Ticket}, telefone {Telefone})",
                meta.TicketId ?? "-", meta.TelefoneCliente ?? meta.Numero ?? "-");
        MetadataMudou?.Invoke(this, EventArgs.Empty);
    }

    public void Iniciar()
    {
        CallMetadata snapshot;
        lock (_lock)
        {
            snapshot = Copiar(_metadataCorrente);
            snapshot.IniciadaEm = DateTimeOffset.Now;
        }
        _recorder.Iniciar(snapshot);
    }

    /// <summary>Para a gravação e envia ao servidor. O resultado chega por evento.</summary>
    public void PararEEnviar()
    {
        var captura = _extensao.Ativa ? _extensao.Encerrar() : _recorder.Parar();
        if (captura is null) return;
        CompletarMetadata(captura.Metadata);
        Enviar(captura, "gravação manual");
        LimparMetadata();
    }

    public void Descartar()
    {
        if (_extensao.Ativa) _extensao.Descartar();
        else _recorder.Descartar();
        EstadoGravacaoMudou?.Invoke(this, false);
        LimparMetadata();
    }

    private void LimparMetadata()
    {
        lock (_lock) _metadataCorrente = CallMetadata.Vazio();
        MetadataMudou?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Completa, no encerramento, o que ainda estiver vazio no snapshot tirado no início.
    /// O atendente frequentemente só abre o ticket do cliente DEPOIS de atender — e é aí
    /// que o cartão do solicitante (e-mail, telefone, nome) aparece no DOM. Sem esta
    /// passada, justamente o dado mais confiável da ligação ficava de fora por chegar
    /// alguns segundos tarde demais.
    /// <para>Só preenche o que está nulo: o valor capturado no início da chamada é o do
    /// ticket certo, e não pode ser sobrescrito se o atendente já navegou para outro.</para>
    /// </summary>
    private void CompletarMetadata(CallMetadata destino)
    {
        lock (_lock)
        {
            destino.Numero ??= _metadataCorrente.Numero;
            destino.TicketId ??= _metadataCorrente.TicketId;
            destino.Status ??= _metadataCorrente.Status;
            destino.Atendente ??= _metadataCorrente.Atendente;
            destino.EmailCliente ??= _metadataCorrente.EmailCliente;
            destino.TelefoneCliente ??= _metadataCorrente.TelefoneCliente;
            destino.NomeCliente ??= _metadataCorrente.NomeCliente;
        }
    }

    /// <summary>Snapshot dos metadados no instante em que a gravação começa: a extensão
    /// continua atualizando <c>_metadataCorrente</c> enquanto a chamada corre.</summary>
    private static CallMetadata Copiar(CallMetadata origem) => new()
    {
        Numero = origem.Numero,
        TicketId = origem.TicketId,
        Status = origem.Status,
        Atendente = origem.Atendente,
        EmailCliente = origem.EmailCliente,
        TelefoneCliente = origem.TelefoneCliente,
        NomeCliente = origem.NomeCliente,
        IniciadaEm = origem.IniciadaEm,
        EncerradaEm = origem.EncerradaEm,
    };
}
