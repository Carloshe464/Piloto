using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Piloto.Core.Abstractions;
using Piloto.Core.Configuration;
using Piloto.Core.Models;
using Piloto.Remote.Contrato;

namespace Piloto.Remote;

/// <summary>
/// Cliente HTTP do servidor de transcrição (contrato 2.0). Fala as três rotas que o piloto
/// usa — <c>/v1/saude</c>, <c>POST /v1/transcricoes</c> e o long-poll do resultado — e
/// traduz toda falha em <see cref="TranscricaoException"/> classificada, que é o que
/// permite à fila distinguir "o servidor caiu" de "o servidor recusou".
/// </summary>
public sealed class ServidorTranscricaoClient : IDisposable
{
    private readonly AppSettings _settings;
    private readonly ILogger<ServidorTranscricaoClient> _log;
    private readonly HttpClient _http;

    /// <summary>Teto do long-poll aceito pelo servidor.</summary>
    public const int EsperaMaximaSegundos = 120;

    public ServidorTranscricaoClient(AppSettings settings, ILogger<ServidorTranscricaoClient> log)
    {
        _settings = settings;
        _log = log;

        var handler = new SocketsHttpHandler
        {
            // Reciclar a conexão faz o cliente reagir a mudança de DNS/IP do servidor sem
            // precisar reiniciar o app — o servidor é uma máquina da rede, não um serviço fixo.
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };

        _http = new HttpClient(handler)
        {
            BaseAddress = new Uri(BaseUrl(settings.Servidor.Url)),
            // Precisa ser maior que o long-poll (120 s) e comportar o upload de uma
            // ligação longa (90 min de WAV 16 kHz ≈ 170 MB).
            Timeout = TimeSpan.FromSeconds(Math.Max(60, settings.Servidor.TimeoutSegundos)),
        };
    }

    public string Url => _http.BaseAddress?.ToString() ?? _settings.Servidor.Url;

    private static string BaseUrl(string url)
    {
        var limpo = string.IsNullOrWhiteSpace(url) ? "http://localhost:8600" : url.Trim();
        return limpo.EndsWith('/') ? limpo : limpo + "/";
    }

    // ------------------------------------------------------------------- Saúde

    /// <summary>
    /// Teste de conectividade <b>e</b> de capacidade. Não exige token — é o que permite
    /// diagnosticar "servidor fora do ar" separado de "token errado".
    /// </summary>
    public async Task<ServidorSaude> SaudeAsync(CancellationToken ct = default)
    {
        using var resposta = await EnviarAsync(new HttpRequestMessage(HttpMethod.Get, "v1/saude"), ct)
            .ConfigureAwait(false);
        var corpo = await LerCorpoAsync(resposta, ct).ConfigureAwait(false);
        GarantirSucesso(resposta, corpo, ehGet: true);

        var dto = Desserializar<SaudeDto>(corpo);
        var saude = MapeadorContrato.MapearSaude(dto);

        if (!saude.ContratoCompativel)
        {
            _log.LogWarning(
                "Servidor fala o contrato {Servidor}; este cliente conhece o {Cliente}. " +
                "A transcrição segue (canais é a parte estável), mas diálogo, campos e resumo do servidor serão ignorados.",
                saude.VersaoContrato ?? "?", ServidorSaude.ContratoSuportado);
        }

        return saude;
    }

    // ------------------------------------------------------------------- Envio

    /// <summary>
    /// Envia os dois canais e devolve o job aceito (202). O cabeçalho
    /// <c>Idempotency-Key</c> vai sempre, com o <c>ligacaoId</c>: é ele que faz a
    /// retentativa reaproveitar o job em vez de gastar uma segunda passada de GPU.
    /// </summary>
    public async Task<JobAceito> EnviarAsync(
        AudioCapture captura,
        ListasFechadas listas,
        string? glossario,
        CancellationToken ct = default)
    {
        // Disposto em qualquer saída — é ele que segura os FileStream dos dois canais.
        using var conteudo = new MultipartFormDataContent();
        var canais = 0;
        canais += AdicionarCanal(conteudo, "atendente", captura.CaminhoAtendente);
        canais += AdicionarCanal(conteudo, "cliente", captura.CaminhoCliente);

        if (canais == 0)
        {
            // Nada para enviar não vira retentativa: os WAVs não vão reaparecer.
            throw new TranscricaoException(FalhaTranscricao.Definitiva,
                "Nenhum arquivo de áudio encontrado para esta ligação (os dois canais estão ausentes em disco).");
        }

        conteudo.Add(new StringContent(captura.LigacaoId, Encoding.UTF8), "ligacaoId");
        conteudo.Add(Json(SerializarMetadados(captura.Metadata)), "metadados");
        conteudo.Add(Json(JsonSerializer.Serialize(listas)), "listas");
        if (!string.IsNullOrWhiteSpace(glossario))
            conteudo.Add(new StringContent(glossario, Encoding.UTF8), "glossario");

        using var requisicao = new HttpRequestMessage(HttpMethod.Post, "v1/transcricoes") { Content = conteudo };
        requisicao.Headers.TryAddWithoutValidation("Idempotency-Key", captura.LigacaoId);

        using var resposta = await EnviarAsync(requisicao, ct).ConfigureAwait(false);
        var corpo = await LerCorpoAsync(resposta, ct).ConfigureAwait(false);
        GarantirSucesso(resposta, corpo, ehGet: false);

        var job = Desserializar<JobDto>(corpo);
        if (string.IsNullOrWhiteSpace(job.JobId))
            throw new TranscricaoException(FalhaTranscricao.Definitiva, "O servidor aceitou o envio mas não devolveu jobId.");

        var aceito = new JobAceito(job.JobId!, job.Estado ?? EstadoJob.Pendente, job.PosicaoNaFila ?? 0);
        _log.LogInformation("Ligação {Ligacao} aceita como job {Job} (posição {Pos} na fila, estado {Estado})",
            captura.LigacaoId, aceito.JobId, aceito.PosicaoNaFila, aceito.Estado);

        return aceito;
    }

    /// <summary>
    /// Long-poll do resultado. <paramref name="esperarAte"/> é <c>transcrito</c> ou
    /// <c>concluido</c>: esperar primeiro por <c>transcrito</c> é o que impede o resumo
    /// de segurar a transcrição.
    /// </summary>
    internal async Task<JobDto> ConsultarAsync(string jobId, int esperarSegundos, string esperarAte, CancellationToken ct)
    {
        var espera = Math.Clamp(esperarSegundos, 0, EsperaMaximaSegundos);
        var rota = $"v1/transcricoes/{Uri.EscapeDataString(jobId)}"
                   + $"?esperarSegundos={espera.ToString(CultureInfo.InvariantCulture)}&esperarAte={esperarAte}";

        using var resposta = await EnviarAsync(new HttpRequestMessage(HttpMethod.Get, rota), ct).ConfigureAwait(false);
        var corpo = await LerCorpoAsync(resposta, ct).ConfigureAwait(false);
        GarantirSucesso(resposta, corpo, ehGet: true);
        return Desserializar<JobDto>(corpo);
    }

    // ------------------------------------------------------------------- Interno

    private int AdicionarCanal(MultipartFormDataContent conteudo, string campo, string caminho)
    {
        if (string.IsNullOrWhiteSpace(caminho) || !File.Exists(caminho))
        {
            _log.LogWarning("Canal {Campo}: arquivo ausente ({Caminho}) — a ligação segue com o outro canal", campo, caminho);
            return 0;
        }

        // WAV só com cabeçalho (loopback que não capturou nada) NÃO é erro: o servidor
        // devolve o canal com vazio=true e a ligação segue. Vai como está, de propósito.
        var stream = File.OpenRead(caminho);
        var arquivo = new StreamContent(stream);
        arquivo.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        conteudo.Add(arquivo, campo, Path.GetFileName(caminho));
        return 1;
    }

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    /// <summary>
    /// Metadados no formato do contrato. <c>OrigemJson</c> fica de fora de propósito: é o
    /// payload bruto da extensão, guardado aqui para auditoria — no servidor só duplicaria
    /// dado pessoal em trânsito, sem nenhum uso.
    /// </summary>
    private static string SerializarMetadados(CallMetadata m)
    {
        var dados = new Dictionary<string, object?>
        {
            ["numero"] = m.Numero,
            ["ticketId"] = m.TicketId,
            ["status"] = m.Status,
            ["atendente"] = m.Atendente,
            ["iniciadaEm"] = m.IniciadaEm?.ToString("o", CultureInfo.InvariantCulture),
            ["encerradaEm"] = m.EncerradaEm?.ToString("o", CultureInfo.InvariantCulture),
            ["emailCliente"] = m.EmailCliente,
            ["telefoneCliente"] = m.TelefoneCliente,
            ["nomeCliente"] = m.NomeCliente,
            ["avisosCaptura"] = m.AvisosCaptura,
        };

        var presentes = dados.Where(kv => kv.Value is not null).ToDictionary(kv => kv.Key, kv => kv.Value);
        return JsonSerializer.Serialize(presentes, new JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
    }

    private async Task<HttpResponseMessage> EnviarAsync(HttpRequestMessage requisicao, CancellationToken ct)
    {
        var token = _settings.Servidor.Token;
        if (!string.IsNullOrWhiteSpace(token))
            requisicao.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());

        try
        {
            return await _http.SendAsync(requisicao, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // encerrando o app: não é falha do servidor
        }
        catch (TaskCanceledException ex)
        {
            // Sem cancelamento nosso, TaskCanceledException é o timeout do HttpClient.
            throw new TranscricaoException(FalhaTranscricao.Transitoria,
                $"Tempo esgotado falando com {Url} ({_settings.Servidor.TimeoutSegundos} s).", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new TranscricaoException(FalhaTranscricao.Transitoria,
                $"Servidor de transcrição inacessível em {Url}: {ex.Message}", ex);
        }
    }

    private static async Task<string> LerCorpoAsync(HttpResponseMessage resposta, CancellationToken ct)
    {
        try { return await resposta.Content.ReadAsStringAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            throw new TranscricaoException(FalhaTranscricao.Transitoria, "Falha ao ler a resposta do servidor.", ex);
        }
    }

    /// <summary>Converte um status de erro em <see cref="TranscricaoException"/> classificada
    /// — a regra está em <see cref="ClassificacaoHttp"/>, que tem teste próprio.</summary>
    private void GarantirSucesso(HttpResponseMessage resposta, string corpo, bool ehGet)
    {
        if (resposta.IsSuccessStatusCode) return;

        var codigo = (int)resposta.StatusCode;
        var tipo = ClassificacaoHttp.Classificar(codigo, ehGet);
        var mensagem = ClassificacaoHttp.Mensagem(codigo, ehGet, Resumir(corpo));

        _log.LogError("Servidor respondeu {Codigo} ({Tipo}): {Mensagem}", codigo, tipo, mensagem);
        throw new TranscricaoException(tipo, mensagem);
    }

    private static T Desserializar<T>(string corpo)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(corpo, ContratoJson.Opts)
                   ?? throw new JsonException("corpo vazio");
        }
        catch (JsonException ex)
        {
            // Resposta ilegível costuma ser proxy/portal cativo no meio do caminho, não o
            // servidor: transitório, porque a rede pode voltar ao normal.
            throw new TranscricaoException(FalhaTranscricao.Transitoria,
                $"Resposta ilegível do servidor: {ex.Message}", ex);
        }
    }

    /// <summary>Trecho curto do corpo do erro — nunca o corpo inteiro, que pode trazer
    /// transcrição e portanto dado pessoal para o log.</summary>
    private static string Resumir(string corpo)
    {
        if (string.IsNullOrWhiteSpace(corpo)) return "(sem detalhes)";
        var limpo = corpo.Trim().ReplaceLineEndings(" ");
        return limpo.Length <= 200 ? limpo : limpo[..200] + "…";
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>Job aceito pelo servidor (resposta 202).</summary>
public sealed record JobAceito(string JobId, string Estado, int PosicaoNaFila);
