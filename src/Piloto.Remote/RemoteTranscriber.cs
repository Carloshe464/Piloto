using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Piloto.Core.Abstractions;
using Piloto.Core.Configuration;
using Piloto.Core.Models;
using Piloto.Remote.Contrato;

namespace Piloto.Remote;

/// <summary>
/// Transcritor de produção: envia os dois canais ao servidor e devolve o trabalho pronto.
/// <para>
/// Entra no lugar do <c>WhisperTranscriber</c> sem que o <c>TranscriptionPipeline</c>
/// precise saber — a emenda é o <see cref="ITranscriber"/>. O que muda de verdade é o
/// custo: a máquina do atendente deixa de carregar modelo, de disputar RAM e de decidir
/// o que cabe nela.
/// </para>
/// </summary>
public sealed class RemoteTranscriber : ITranscriber
{
    private readonly ServidorTranscricaoClient _cliente;
    private readonly ServidorSaudeMonitor _saude;
    private readonly Func<ListasFechadas> _listasProvider;
    private readonly Func<string?> _glossarioProvider;
    private readonly ILogger<RemoteTranscriber> _log;

    /// <summary>Espera por chamada. O servidor aceita até 120 s; usar o teto reduz o
    /// vaivém sem custo — a conexão fica aberta esperando, não em polling.</summary>
    private const int EsperaPorChamadaSegundos = 30;

    /// <summary>
    /// Teto do tempo total esperando um job. Existe para o caso patológico (job travado no
    /// servidor): passa disso e a ligação volta para a fila em vez de segurar o processador.
    /// </summary>
    private static readonly TimeSpan EsperaTotalMaxima = TimeSpan.FromMinutes(30);

    public RemoteTranscriber(
        ServidorTranscricaoClient cliente,
        ServidorSaudeMonitor saude,
        Func<ListasFechadas> listasProvider,
        Func<string?> glossarioProvider,
        ILogger<RemoteTranscriber> log)
    {
        _cliente = cliente;
        _saude = saude;
        _listasProvider = listasProvider;
        _glossarioProvider = glossarioProvider;
        _log = log;
    }

    public async Task<TranscriptionResult> TranscreverAsync(AudioCapture captura, CancellationToken ct = default)
    {
        // Capacidades ANTES de mandar áudio: é o que decide se o resultado vem com campos e
        // resumo prontos, ou se as camadas locais assumem. Se a consulta falhar, segue com o
        // que se sabia (ou nada) — quem classifica a falha de verdade é a resposta do POST.
        var saude = await _saude.ObterAsync(ct).ConfigureAwait(false);

        var relogio = Stopwatch.StartNew();
        var job = await _cliente.EnviarAsync(captura, _listasProvider(), _glossarioProvider(), ct)
            .ConfigureAwait(false);

        // Fase 1 — a transcrição. É o que impede o resumo de segurar a ligação.
        var resposta = await EsperarAsync(job.JobId, EstadoJob.Transcrito, ct).ConfigureAwait(false);
        _log.LogInformation("Ligação {Ligacao}: transcrita em {Seg:0.0} s", captura.LigacaoId, relogio.Elapsed.TotalSeconds);

        // Fase 2 — o resumo, só quando o servidor de fato o gera. Um resumo que falhe conclui
        // o job assim mesmo, com a transcrição inteira e resumoEstado "erro".
        if ((saude?.ResumoUtilizavel ?? false) && !EstadoJob.Alcancou(resposta.Estado, EstadoJob.Concluido))
        {
            resposta = await EsperarAsync(job.JobId, EstadoJob.Concluido, ct).ConfigureAwait(false);
            _log.LogInformation("Ligação {Ligacao}: resumo do servidor em {Seg:0.0} s (estado {Estado})",
                captura.LigacaoId, relogio.Elapsed.TotalSeconds, resposta.ResumoEstadoOuVazio());
        }

        var resultado = MapeadorContrato.Mapear(resposta.Resultado, saude);
        _log.LogInformation("Ligação {Ligacao}: {Segmentos} segmento(s) de {Origem}; campos {Campos}, resumo {Resumo}",
            captura.LigacaoId, resultado.Transcript.Segmentos.Count, resultado.Origem,
            resultado.Campos is null ? "locais" : "do servidor",
            resultado.Resumo is null ? "local" : "do servidor");

        return resultado;
    }

    private async Task<JobDto> EsperarAsync(string jobId, string alvo, CancellationToken ct)
    {
        var limite = DateTimeOffset.Now + EsperaTotalMaxima;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var job = await _cliente.ConsultarAsync(jobId, EsperaPorChamadaSegundos, alvo, ct).ConfigureAwait(false);

            if (string.Equals(job.Estado, EstadoJob.Erro, StringComparison.Ordinal))
                throw new TranscricaoException(FalhaTranscricao.Processamento,
                    $"O servidor terminou o job em erro: {job.Erro ?? "sem detalhes"}");

            if (EstadoJob.Alcancou(job.Estado, alvo))
                return job;

            if (EstadoJob.Ordem(job.Estado) < 0)
                throw new TranscricaoException(FalhaTranscricao.Definitiva,
                    $"O servidor devolveu um estado que este cliente não conhece: \"{job.Estado}\".");

            if (DateTimeOffset.Now > limite)
                throw new TranscricaoException(FalhaTranscricao.Transitoria,
                    $"O servidor não concluiu a ligação em {EsperaTotalMaxima.TotalMinutes:0} min (estado: {job.Estado}) — de volta para a fila.");
        }
    }
}

internal static class JobDtoExtensions
{
    public static string ResumoEstadoOuVazio(this JobDto job) => job.Resultado?.ResumoEstado ?? "?";
}
