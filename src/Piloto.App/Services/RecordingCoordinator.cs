using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Piloto.Audio;
using Piloto.Bridge;
using Piloto.Core.Abstractions;
using Piloto.Core.Configuration;
using Piloto.Core.Models;
using Piloto.Core.Services;

namespace Piloto.App.Services;

/// <summary>
/// Orquestra a gravação: mantém os metadados correntes (via bridge), controla o gravador
/// WASAPI manual (botão) e a captura automática pela extensão (hook WebRTC) — esta começa
/// e termina sozinha nas fronteiras reais da chamada e enfileira ao encerrar.
/// </summary>
public sealed class RecordingCoordinator : IDisposable
{
    /// <summary>De quanto em quanto tempo reconfere se ticket e telefone já chegaram.</summary>
    private static readonly TimeSpan IntervaloVerificacao = TimeSpan.FromMilliseconds(500);

    private readonly IAudioRecorder _recorder;
    private readonly ExtensionAudioRecorder _extensao;
    private readonly ClickWriteUploader _uploader;
    private readonly SincronizadorServidor _sincronizador;
    private readonly ZendeskBridgeServer _bridge;
    private readonly ICallRepository _repo;
    private readonly AppSettings _settings;
    private readonly ILogger<RecordingCoordinator> _log;
    private readonly object _lock = new();

    private CallMetadata _metadataCorrente = CallMetadata.Vazio();
    private EsperaIdentificacao? _espera;

    /// <summary>
    /// Uma captura encerrada que ainda não subiu, à espera do ticket. Vive fora do
    /// <see cref="_metadataCorrente"/> de propósito: o áudio já está fechado em disco e não
    /// pode ser contaminado se o atendente atender outra ligação durante a espera.
    /// </summary>
    private sealed class EsperaIdentificacao
    {
        public required AudioCapture Captura { get; init; }
        public required string Origem { get; init; }

        /// <summary>0 = esperando, 1 = já enviada. Manipulado por <c>Interlocked</c>:
        /// o prazo e uma chamada nova podem concluí-la ao mesmo tempo.</summary>
        public int Concluida;
    }

    /// <summary>Estado do gravador manual (botão da UI); a sessão da extensão é
    /// autônoma e sinalizada apenas via <see cref="EstadoGravacaoMudou"/>.</summary>
    public bool EstaGravando => _recorder.EstaGravando || _extensao.Ativa;
    public CallMetadata MetadataCorrente { get { lock (_lock) return _metadataCorrente; } }

    public event EventHandler<bool>? EstadoGravacaoMudou;

    /// <summary>
    /// A captura automática começou (<c>true</c>) ou terminou (<c>false</c>).
    /// <para>
    /// Existe separado de <see cref="EstadoGravacaoMudou"/> porque só a captura automática
    /// precisa de aviso visível: na gravação manual o atendente acabou de clicar no botão e
    /// já sabe. Trocar o ícone da bandeja não basta para a automática — ela começa sozinha,
    /// e ninguém repara num ícone de 16 px mudando de cor enquanto atende.
    /// </para>
    /// </summary>
    public event EventHandler<bool>? CapturaAutomaticaMudou;
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
        AppSettings settings,
        ILogger<RecordingCoordinator> log)
    {
        _recorder = recorder;
        _extensao = extensao;
        _uploader = uploader;
        _sincronizador = sincronizador;
        _bridge = bridge;
        _repo = repo;
        _settings = settings;
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

        // A ligação que começa agora não pode herdar o ticket da anterior, nem deixá-la
        // esperando indefinidamente: daqui para frente o que chegar é da chamada nova.
        ConcluirEsperaPendente();

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
        CapturaAutomaticaMudou?.Invoke(this, true);
    }

    private void EncerrarSessaoExtensao()
    {
        if (!_extensao.Ativa) return;

        var captura = _extensao.Encerrar();
        EstadoGravacaoMudou?.Invoke(this, false);
        CapturaAutomaticaMudou?.Invoke(this, false);
        if (captura is null) return;

        _log.LogInformation("Gravação automática encerrada ({Duracao})", captura.Duracao);
        AguardarIdentificacao(captura, "captura automática pela extensão");
    }

    /// <summary>
    /// Segura a captura até ticket e telefone chegarem, e só então envia.
    /// <para>
    /// O ticket é aberto alguns segundos DEPOIS de a ligação cair, e o servidor grava
    /// ticket e telefone no instante do enfileiramento — o <c>PATCH</c> de correção alcança
    /// campos objetivos e resumo, mas não os metadados. Enviar na hora do encerramento,
    /// como era antes, entregava sem ticket exatamente a ligação que tinha um.
    /// </para>
    /// <para>
    /// Não bloqueia: quem chama é o callback do bridge, e prendê-lo travaria o recebimento
    /// da próxima chamada. A espera corre em segundo plano e termina assim que os dois
    /// dados aparecem — o prazo do <c>appsettings.json</c> é teto, não atraso fixo.
    /// </para>
    /// </summary>
    private void AguardarIdentificacao(AudioCapture captura, string origem)
    {
        // Espera anterior ainda de pé (duas ligações em sequência muito rápida): fecha a
        // antiga primeiro, com o que ela tiver. Duas disputando o mesmo _metadataCorrente
        // trocariam o ticket de uma pela da outra.
        ConcluirEsperaPendente();

        var espera = new EsperaIdentificacao { Captura = captura, Origem = origem };
        Interlocked.Exchange(ref _espera, espera);

        var limite = TimeSpan.FromSeconds(Math.Max(0, _settings.Captura.EsperaIdentificacaoSegundos));
        if (limite <= TimeSpan.Zero)
        {
            Concluir(espera);
            return;
        }

        if (Identificada())
        {
            // O ticket já estava na tela quando a ligação caiu. Nada a esperar.
            Concluir(espera);
            return;
        }

        _log.LogInformation(
            "Aguardando até {Segundos:0}s pelo ticket antes de enviar a gravação",
            limite.TotalSeconds);

        _ = Task.Run(async () =>
        {
            try
            {
                var relogio = Stopwatch.StartNew();
                while (relogio.Elapsed < limite && Volatile.Read(ref espera.Concluida) == 0)
                {
                    if (Identificada())
                    {
                        _log.LogInformation("Ticket identificado após {Segundos:0.0}s de espera",
                                            relogio.Elapsed.TotalSeconds);
                        break;
                    }
                    await Task.Delay(IntervaloVerificacao).ConfigureAwait(false);
                }

                if (!Identificada())
                    _log.LogWarning(
                        "Prazo de {Segundos:0}s esgotado sem ticket — enviando com o que há",
                        limite.TotalSeconds);
            }
            catch (Exception ex)
            {
                // A gravação nunca pode ficar presa por causa da espera: qualquer falha
                // aqui cai direto no envio.
                _log.LogError(ex, "Falha ao aguardar o ticket; enviando a gravação assim mesmo");
            }
            finally
            {
                Concluir(espera);
            }
        });
    }

    /// <summary>Ticket e telefone já conhecidos — não há mais o que esperar.</summary>
    private bool Identificada()
    {
        lock (_lock)
            return !string.IsNullOrWhiteSpace(_metadataCorrente.TicketId)
                && !string.IsNullOrWhiteSpace(_metadataCorrente.TelefoneCliente
                                              ?? _metadataCorrente.Numero);
    }

    /// <summary>
    /// Completa os metadados e envia. Idempotente: o prazo, uma ligação nova e o
    /// encerramento do app podem chegar aqui ao mesmo tempo, e só o primeiro envia.
    /// </summary>
    private void Concluir(EsperaIdentificacao espera)
    {
        if (Interlocked.Exchange(ref espera.Concluida, 1) == 1) return;
        Interlocked.CompareExchange(ref _espera, null, espera);

        CompletarMetadata(espera.Captura.Metadata);
        LimparMetadata();
        Enviar(espera.Captura, espera.Origem);
    }

    /// <summary>
    /// Fecha na hora a captura que estiver esperando. Chamado quando a espera perde o
    /// sentido: outra ligação começou, o atendente mandou enviar, ou o app está fechando.
    /// </summary>
    public void ConcluirEsperaPendente()
    {
        if (Volatile.Read(ref _espera) is { } pendente)
            Concluir(pendente);
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
        // Clique explícito em "enviar" encerra qualquer espera: o atendente mandou subir
        // agora, e continuar contando o prazo contrariaria a ordem dele.
        ConcluirEsperaPendente();

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

    /// <summary>
    /// App fechando: envia na hora o que estava esperando ticket.
    /// <para>
    /// Sem isto, fechar o programa durante a espera abandonaria a captura — o áudio ficaria
    /// em disco sem registro nenhum e sem ninguém para reenviá-lo, porque o item da fila só
    /// nasce dentro de <see cref="Enviar"/>. Perder a ligação para ganhar o ticket seria um
    /// mau negócio.
    /// </para>
    /// </summary>
    public void Dispose() => ConcluirEsperaPendente();
}
