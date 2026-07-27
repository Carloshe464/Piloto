using System.Text.Json;
using Microsoft.Extensions.Logging;
using Piloto.Core.Abstractions;
using Piloto.Core.Configuration;
using Piloto.Core.Models;

namespace Piloto.Core.Pipeline;

/// <summary>
/// Consome a fila persistida (SQLite) processando <b>uma ligação por vez</b>.
/// <para>
/// Depois da migração para o servidor, esta classe é a garantia de que nenhuma ligação se
/// perde: não há mais transcrição local para onde cair, então servidor fora do ar significa
/// <b>enfileirar e reenviar</b> — nunca descartar. Por isso falha de rede não consome
/// tentativa (ver <see cref="FalhaTranscricao"/>): só recusa do servidor e erro de
/// processamento contam.
/// </para>
/// </summary>
public sealed class QueueProcessor : IAsyncDisposable
{
    private readonly ICallRepository _repo;
    private readonly TranscriptionPipeline _pipeline;
    private readonly AppSettings _settings;
    private readonly ILogger<QueueProcessor> _log;

    private CancellationTokenSource? _cts;
    private Task? _loop;

    private static readonly TimeSpan IntervaloOcioso = TimeSpan.FromSeconds(3);

    // Após este tempo sem itens, o LLM é devolvido ao SO (~2,4 GB do 4B): o atendente não
    // paga o aluguel de RAM o dia inteiro por algo que trabalha minutos. Em sequência de
    // ligações o cache continua valendo — só descarrega na calmaria.
    private static readonly TimeSpan DescargaAposOciosidade = TimeSpan.FromMinutes(5);

    private DateTimeOffset _ultimaAtividade = DateTimeOffset.Now;
    private bool _modelosDescarregados;

    /// <summary>Tentativas antes de a ligação ir para revisão humana.</summary>
    private int MaxTentativas => Math.Max(1, _settings.Servidor.MaxTentativas);

    public event EventHandler<CallRecord>? RegistroProcessado;
    public event EventHandler? FilaMudou;

    /// <summary>Disparado quando um item começa a ser processado (id do item) — a UI
    /// mostra que o trabalho está em andamento em vez de silêncio.</summary>
    public event EventHandler<long>? ItemIniciado;

    /// <summary>
    /// Disparado quando um envio falha por indisponibilidade do servidor, com o motivo e o
    /// instante da próxima tentativa. A UI usa para dizer ao atendente que a ligação está
    /// guardada — sem isso, "nada aconteceu" é indistinguível de "a ligação se perdeu".
    /// </summary>
    public event EventHandler<(string Motivo, DateTimeOffset Proxima)>? EnvioAdiado;

    public QueueProcessor(ICallRepository repo, TranscriptionPipeline pipeline, AppSettings settings, ILogger<QueueProcessor> log)
    {
        _repo = repo;
        _pipeline = pipeline;
        _settings = settings;
        _log = log;
    }

    public bool EmExecucao => _loop is { IsCompleted: false };

    public void Iniciar()
    {
        if (EmExecucao) return;

        // Itens presos em Processando são órfãos de um encerramento abrupto (crash);
        // sem isso, nunca mais seriam tentados — ProximoPendente só busca Pendente.
        try
        {
            var orfaos = _repo.RecuperarItensOrfaos(MaxTentativas);
            if (orfaos > 0)
                _log.LogWarning("Fila: {N} item(ns) órfão(s) de queda anterior recuperado(s) — após {Max} quedas o item vai para Erro",
                    orfaos, MaxTentativas);
        }
        catch (Exception ex) { _log.LogError(ex, "Falha ao recuperar itens órfãos da fila"); }

        MaterializarItensComErro();

        _cts = new CancellationTokenSource();
        _loop = Task.Factory.StartNew(
            () => LoopAsync(_cts.Token),
            _cts.Token,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default).Unwrap();
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        _log.LogInformation("QueueProcessor iniciado");
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var item = _repo.ProximoPendente();
                if (item is null)
                {
                    // Fila vazia é a janela para completar resumos que falharam por falta
                    // de memória no pico: os registros "se curam" quando a máquina folga.
                    if (!await TentarResumoPendenteAsync(ct).ConfigureAwait(false))
                    {
                        DescarregarSeOcioso();
                        await EsperarAsync(ct).ConfigureAwait(false);
                    }
                    continue;
                }

                await ProcessarItemAsync(item, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.LogError(ex, "Erro inesperado no loop da fila");
                await EsperarAsync(ct).ConfigureAwait(false);
            }
        }
        _log.LogInformation("QueueProcessor encerrado");
    }

    private async Task ProcessarItemAsync(QueueItem item, CancellationToken ct)
    {
        item.Estado = QueueState.Processando;
        item.AtualizadoEm = DateTimeOffset.Now;
        _repo.AtualizarItem(item);
        FilaMudou?.Invoke(this, EventArgs.Empty);
        ItemIniciado?.Invoke(this, item.Id);

        try
        {
            var captura = ReconstruirCaptura(item);
            var registro = await _pipeline.ProcessarAsync(captura, ct).ConfigureAwait(false);

            if (item.RegistroId is long idExistente)
            {
                // Reprocessamento pedido pela UI: substitui o conteúdo do registro
                // original — id/uuid/criado_em estáveis, ligação nunca duplicada.
                var original = _repo.ObterRegistro(idExistente);
                registro.Id = idExistente;
                if (original is not null)
                {
                    registro.Uuid = original.Uuid;
                    registro.CriadoEm = original.CriadoEm;
                }
                _repo.AtualizarRegistro(registro);
            }
            else
            {
                registro.Id = _repo.SalvarRegistro(registro);
            }

            item.Estado = QueueState.Concluido;
            item.RegistroId = registro.Id;
            item.ProximaTentativaEm = null;
            item.AtualizadoEm = DateTimeOffset.Now;
            _repo.AtualizarItem(item);

            RegistroProcessado?.Invoke(this, registro);
            FilaMudou?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            RegistrarFalha(item, ex);
        }
        finally
        {
            _ultimaAtividade = DateTimeOffset.Now;
            _modelosDescarregados = false;
            // Evidência para calibrar consumo em produção (memória de pico vs. de posse).
            _log.LogInformation("Memória do processo após o item {Id}: {Mb} MB",
                item.Id, Environment.WorkingSet / 1_048_576);
        }
    }

    /// <summary>
    /// Traduz a falha em destino do item. A distinção que importa: <b>4xx são definitivos</b>
    /// (retentar só repete o erro) e <b>falha de rede é transitória</b> (retentar é o
    /// comportamento certo, e a idempotência do servidor torna isso barato).
    /// </summary>
    private void RegistrarFalha(QueueItem item, Exception ex)
    {
        item.UltimoErro = ex.Message;
        item.AtualizadoEm = DateTimeOffset.Now;
        item.ProximaTentativaEm = null;

        var tipo = (ex as TranscricaoException)?.Tipo;
        switch (tipo)
        {
            case FalhaTranscricao.Transitoria:
                // O áudio está em disco e o problema não é dele. Não conta tentativa: com
                // laço de 3 s, 10 segundos de servidor fora do ar apagariam a ligação.
                item.Estado = QueueState.Pendente;
                item.ProximaTentativaEm = DateTimeOffset.Now + Recuo(item);
                _repo.AtualizarItem(item);
                _log.LogWarning("Item {Id}: servidor indisponível ({Erro}). Nova tentativa em {Quando:HH:mm:ss}",
                    item.Id, ex.Message, item.ProximaTentativaEm);
                EnvioAdiado?.Invoke(this, (ex.Message, item.ProximaTentativaEm.Value));
                FilaMudou?.Invoke(this, EventArgs.Empty);
                return;

            case FalhaTranscricao.Reenviar:
                // Resultado expirou no servidor (900 s). Reenviar do zero é a saída — conta
                // tentativa só para o laço ter fim se o servidor insistir em não achar o job.
                item.Tentativas++;
                item.Estado = item.Tentativas >= MaxTentativas ? QueueState.Erro : QueueState.Pendente;
                _log.LogWarning("Item {Id}: resultado expirou no servidor — reenviando o áudio (tentativa {N})",
                    item.Id, item.Tentativas);
                break;

            case FalhaTranscricao.Definitiva:
                // O servidor recusou e recusaria de novo (400/401/413/415). Vai direto para
                // revisão humana com o motivo, sem gastar mais duas passadas idênticas.
                item.Tentativas = MaxTentativas;
                item.Estado = QueueState.Erro;
                _log.LogError(ex, "Item {Id}: servidor recusou definitivamente — sem retentativa", item.Id);
                break;

            case FalhaTranscricao.Processamento:
            default:
                // Erro do job no servidor, ou falha não classificada (bug do cliente, disco,
                // banco): comportamento histórico — tenta de novo até o teto.
                item.Tentativas++;
                item.Estado = item.Tentativas >= MaxTentativas ? QueueState.Erro : QueueState.Pendente;
                _log.LogError(ex, "Falha ao processar item {Id} (tentativa {N})", item.Id, item.Tentativas);
                break;
        }

        _repo.AtualizarItem(item);
        if (item.Estado == QueueState.Erro)
            MaterializarItensComErro();
        FilaMudou?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Recuo antes de reenviar, derivado da idade do item — sem coluna extra e sem
    /// martelar o servidor: quem acabou de chegar tenta rápido (a queda pode ser um
    /// reinício de serviço); quem já espera há dez minutos tenta de dez em dez.
    /// </summary>
    private static TimeSpan Recuo(QueueItem item)
    {
        var idade = DateTimeOffset.Now - item.CriadoEm;
        if (idade < TimeSpan.FromMinutes(2)) return TimeSpan.FromSeconds(30);
        if (idade < TimeSpan.FromMinutes(10)) return TimeSpan.FromMinutes(2);
        return TimeSpan.FromMinutes(10);
    }

    /// <summary>Entre varreduras frustradas (nada pendente, ou LLM ainda ausente) não
    /// adianta reconsultar a cada tique de 3 s — o quadro só muda em minutos.</summary>
    private static readonly TimeSpan IntervaloVarreduraResumos = TimeSpan.FromMinutes(10);
    private DateTimeOffset _proximaVarreduraResumos = DateTimeOffset.MinValue;

    /// <summary>
    /// Completa UM resumo pendente por vez (registros cujo LLM falhou na hora, mas cuja
    /// transcrição está salva). Devolve true quando concluiu um — o loop tenta o próximo
    /// imediatamente, aproveitando o modelo já carregado.
    /// </summary>
    private async Task<bool> TentarResumoPendenteAsync(CancellationToken ct)
    {
        if (DateTimeOffset.Now < _proximaVarreduraResumos) return false;

        CallRecord? registro = null;
        try { registro = _repo.RegistrosComResumoPendente(1).FirstOrDefault(); }
        catch (Exception ex) { _log.LogError(ex, "Falha ao buscar resumos pendentes"); }

        if (registro is null)
        {
            _proximaVarreduraResumos = DateTimeOffset.Now + IntervaloVarreduraResumos;
            return false;
        }

        try
        {
            if (!await _pipeline.TentarResumoPendenteAsync(registro, ct).ConfigureAwait(false))
            {
                // Sem LLM neste momento — o quadro muda, tenta mais tarde.
                _proximaVarreduraResumos = DateTimeOffset.Now + IntervaloVarreduraResumos;
                return false;
            }

            _repo.AtualizarRegistro(registro);
            RegistroProcessado?.Invoke(this, registro);
            _ultimaAtividade = DateTimeOffset.Now;
            _modelosDescarregados = false;
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.LogError(ex, "Falha ao completar resumo pendente do registro {Id}", registro.Id);
            _proximaVarreduraResumos = DateTimeOffset.Now + IntervaloVarreduraResumos;
            return false;
        }
    }

    private void DescarregarSeOcioso()
    {
        if (_modelosDescarregados) return;
        if (DateTimeOffset.Now - _ultimaAtividade < DescargaAposOciosidade) return;

        _modelosDescarregados = true;
        try
        {
            if (_pipeline.LiberarModelos())
                _log.LogInformation("Fila ociosa há {Min} min: modelos descarregados (memória do processo: {Mb} MB)",
                    (int)DescargaAposOciosidade.TotalMinutes, Environment.WorkingSet / 1_048_576);
        }
        catch (Exception ex) { _log.LogError(ex, "Falha ao descarregar modelos ociosos"); }
    }

    /// <summary>
    /// Item que esgotou as tentativas nunca chegaria à UI: a ligação simplesmente sumia
    /// (crash nativo derruba o processo sem passar pelo catch, e o item morria em Erro
    /// sem registro). Materializa um registro marcado para revisão — a ligação aparece
    /// no histórico com o motivo, e o áudio fica preservado para conferência.
    /// </summary>
    private void MaterializarItensComErro()
    {
        IReadOnlyList<QueueItem> itens;
        try { itens = _repo.ItensErroSemRegistro(); }
        catch (Exception ex) { _log.LogError(ex, "Falha ao listar itens com erro sem registro"); return; }

        foreach (var item in itens)
        {
            try
            {
                var captura = ReconstruirCaptura(item);
                var registro = new CallRecord
                {
                    Uuid = captura.LigacaoId,
                    Metadata = captura.Metadata,
                    CriadoEm = DateTimeOffset.Now,
                    Duracao = captura.Duracao,
                    CaminhoAudioAtendente = captura.CaminhoAtendente,
                    CaminhoAudioCliente = captura.CaminhoCliente,
                };
                registro.MarcarRevisao(
                    $"Processamento não concluído após {MaxTentativas} tentativa(s). " +
                    $"Último erro: {item.UltimoErro ?? "app encerrado inesperadamente"}. Áudio preservado em disco.");

                registro.Id = _repo.SalvarRegistro(registro);
                item.RegistroId = registro.Id;
                item.AtualizadoEm = DateTimeOffset.Now;
                _repo.AtualizarItem(item);

                _log.LogWarning("Item {Item} esgotou as tentativas — registro {Registro} criado marcado para revisão",
                    item.Id, registro.Id);
                RegistroProcessado?.Invoke(this, registro);
            }
            catch (Exception ex) { _log.LogError(ex, "Falha ao materializar registro do item {Id}", item.Id); }
        }
        if (itens.Count > 0) FilaMudou?.Invoke(this, EventArgs.Empty);
    }

    private static AudioCapture ReconstruirCaptura(QueueItem item)
    {
        var metadata = string.IsNullOrWhiteSpace(item.MetadataJson)
            ? CallMetadata.Vazio()
            : JsonSerializer.Deserialize<CallMetadata>(item.MetadataJson!) ?? CallMetadata.Vazio();

        var iniciada = metadata.IniciadaEm ?? item.CriadoEm;
        var encerrada = metadata.EncerradaEm ?? item.CriadoEm;

        return new AudioCapture
        {
            // Itens gravados antes da migração não têm ligacaoId: um id derivado do item
            // mantém a chave de idempotência estável entre reinícios, que é o que importa.
            LigacaoId = string.IsNullOrWhiteSpace(item.LigacaoId)
                ? $"item-{item.Id}"
                : item.LigacaoId!,
            CaminhoAtendente = item.CaminhoAudioAtendente,
            CaminhoCliente = item.CaminhoAudioCliente,
            IniciadaEm = iniciada,
            EncerradaEm = encerrada,
            Metadata = metadata,
        };
    }

    private static async Task EsperarAsync(CancellationToken ct)
    {
        try { await Task.Delay(IntervaloOcioso, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { /* encerrando */ }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            _cts?.Cancel();
            if (_loop is not null)
                await _loop.ConfigureAwait(false);
        }
        catch { /* ignore */ }
        finally
        {
            _cts?.Dispose();
        }
    }
}
