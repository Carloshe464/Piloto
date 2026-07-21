using System.Text.Json;
using Microsoft.Extensions.Logging;
using Piloto.Core.Abstractions;
using Piloto.Core.Models;

namespace Piloto.Core.Pipeline;

/// <summary>
/// Consome a fila persistida (SQLite) processando <b>uma transcrição por vez</b>, em
/// prioridade baixa, para não travar a máquina do atendente. Pausa automaticamente
/// quando os modelos estão ausentes.
/// </summary>
public sealed class QueueProcessor : IAsyncDisposable
{
    private readonly ICallRepository _repo;
    private readonly TranscriptionPipeline _pipeline;
    private readonly IModelCatalog _modelos;
    private readonly ILogger<QueueProcessor> _log;

    private CancellationTokenSource? _cts;
    private Task? _loop;

    private static readonly TimeSpan IntervaloOcioso = TimeSpan.FromSeconds(3);

    // Após este tempo sem itens, os modelos são devolvidos ao SO (~2,4 GB do 4B): o
    // atendente não paga o aluguel de RAM o dia inteiro por algo que trabalha minutos.
    // Em sequência de ligações o cache continua valendo — só descarrega na calmaria.
    private static readonly TimeSpan DescargaAposOciosidade = TimeSpan.FromMinutes(5);

    private const int MaxTentativas = 3;

    private DateTimeOffset _ultimaAtividade = DateTimeOffset.Now;
    private bool _modelosDescarregados;

    public event EventHandler<CallRecord>? RegistroProcessado;
    public event EventHandler? FilaMudou;

    /// <summary>Disparado quando um item começa a ser processado (id do item) — a UI
    /// mostra que o trabalho está em andamento em vez de silêncio.</summary>
    public event EventHandler<long>? ItemIniciado;

    public QueueProcessor(ICallRepository repo, TranscriptionPipeline pipeline, IModelCatalog modelos, ILogger<QueueProcessor> log)
    {
        _repo = repo;
        _pipeline = pipeline;
        _modelos = modelos;
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
        // Prioridade baixa: a UI e o navegador do atendente continuam responsivos.
        try { Thread.CurrentThread.Priority = ThreadPriority.Lowest; } catch { /* ignore */ }

        _log.LogInformation("QueueProcessor iniciado");
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!_modelos.PipelinePronto)
                {
                    await EsperarAsync(ct).ConfigureAwait(false);
                    continue;
                }

                var item = _repo.ProximoPendente();
                if (item is null)
                {
                    DescarregarSeOcioso();
                    await EsperarAsync(ct).ConfigureAwait(false);
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
            var id = _repo.SalvarRegistro(registro);
            registro.Id = id;

            item.Estado = QueueState.Concluido;
            item.RegistroId = id;
            item.AtualizadoEm = DateTimeOffset.Now;
            _repo.AtualizarItem(item);

            RegistroProcessado?.Invoke(this, registro);
            FilaMudou?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            item.Tentativas++;
            item.UltimoErro = ex.Message;
            item.Estado = item.Tentativas >= MaxTentativas ? QueueState.Erro : QueueState.Pendente;
            item.AtualizadoEm = DateTimeOffset.Now;
            _repo.AtualizarItem(item);
            _log.LogError(ex, "Falha ao processar item {Id} (tentativa {N})", item.Id, item.Tentativas);
            if (item.Estado == QueueState.Erro)
                MaterializarItensComErro();
            FilaMudou?.Invoke(this, EventArgs.Empty);
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
                    Metadata = captura.Metadata,
                    CriadoEm = DateTimeOffset.Now,
                    Duracao = captura.Duracao,
                    CaminhoAudioAtendente = captura.CaminhoAtendente,
                    CaminhoAudioCliente = captura.CaminhoCliente,
                };
                registro.MarcarRevisao(
                    $"Processamento não concluído após {MaxTentativas} tentativa(s) — provável falta de memória na máquina. " +
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
