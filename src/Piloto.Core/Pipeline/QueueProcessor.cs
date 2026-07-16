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
    private const int MaxTentativas = 3;

    public event EventHandler<CallRecord>? RegistroProcessado;
    public event EventHandler? FilaMudou;

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
            var orfaos = _repo.RecuperarItensOrfaos();
            if (orfaos > 0)
                _log.LogWarning("Fila: {N} item(ns) órfão(s) de execução anterior devolvido(s) à fila", orfaos);
        }
        catch (Exception ex) { _log.LogError(ex, "Falha ao recuperar itens órfãos da fila"); }

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
            FilaMudou?.Invoke(this, EventArgs.Empty);
        }
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
