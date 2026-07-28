using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Piloto.Core.Configuration;
using Piloto.Core.Models;

namespace Piloto.Core.Services;

/// <summary>
/// Envia a ligação para o servidor de transcrição e devolve o identificador do registro.
/// Substitui o <c>CallEnqueuer</c>: a inferência deixou de acontecer nesta máquina.
/// <para>
/// O <see cref="Piloto.Audio.WasapiDualChannelRecorder"/> já grava exatamente o que o
/// servidor espera — dois WAV separados, 16 kHz mono PCM16 — então não há conversão aqui.
/// </para>
/// <para>
/// A parte que exige cuidado não é o upload, é a <b>fila local</b>. Servidor fora do ar ou
/// rede instável não podem virar atendimento perdido: a gravação fica em disco e sobe
/// sozinha depois. Sem isso, uma oscilação de rede apaga uma ligação e ninguém descobre.
/// </para>
/// </summary>
public sealed class ClickWriteUploader : IDisposable
{
    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly ServidorSettings _cfg;
    private readonly string _pendentes;
    private readonly ILogger<ClickWriteUploader> _log;
    private readonly SemaphoreSlim _umDeCadaVez = new(1, 1);
    private readonly Timer _reenvio;

    /// <summary>Ligação aceita pelo servidor. O argumento é o call_id.</summary>
    public event EventHandler<RespostaEnvio>? LigacaoAceita;

    /// <summary>Servidor inacessível: a ligação ficou retida em disco.</summary>
    public event EventHandler<string>? EnvioAdiado;

    public ClickWriteUploader(AppSettings settings, ILogger<ClickWriteUploader> log)
    {
        _cfg = settings.Servidor;
        _log = log;
        _pendentes = Path.Combine(settings.PastaDadosExpandida, "pendentes");
        Directory.CreateDirectory(_pendentes);

        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(_cfg.TimeoutSegundos) };
        if (!string.IsNullOrWhiteSpace(_cfg.Token))
            _http.DefaultRequestHeaders.Add("X-Token", _cfg.Token);

        var intervalo = TimeSpan.FromSeconds(_cfg.IntervaloReenvioSegundos);
        _reenvio = new Timer(async _ => await DrenarPendentesAsync().ConfigureAwait(false),
                             null, intervalo, intervalo);
    }

    /// <summary>Envia a captura. Se o servidor não responder, retém e devolve null.</summary>
    public async Task<RespostaEnvio?> EnviarAsync(AudioCapture captura, CancellationToken ct = default)
    {
        var metadados = Converter(captura);
        try
        {
            var resposta = await PostarAsync(
                captura.CaminhoAtendente, captura.CaminhoCliente, metadados, ct).ConfigureAwait(false);
            _log.LogInformation("Ligação aceita pelo servidor: {CallId} (posição {Pos})",
                                resposta.CallId, resposta.Posicao);
            LigacaoAceita?.Invoke(this, resposta);
            return resposta;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            var pasta = Reter(captura, metadados);
            _log.LogWarning("Servidor inacessível ({Erro}); ligação retida em {Pasta}", e.Message, pasta);
            EnvioAdiado?.Invoke(this, pasta);
            return null;
        }
    }

    /// <summary>Traduz os metadados do Zendesk para o contrato do servidor.</summary>
    private MetadadosLigacao Converter(AudioCapture captura)
    {
        var m = captura.Metadata;
        var temCadastro = m.NomeCliente is not null || m.EmailCliente is not null
                          || m.TelefoneCliente is not null;

        return new MetadadosLigacao
        {
            Ticket = m.TicketId,
            Telefone = m.Numero,
            AgentId = m.Atendente,
            IniciadaEm = captura.IniciadaEm,
            // O gravador inicia as duas capturas juntas, então os canais já estão
            // alinhados. NÃO estimar aqui: offset errado desalinha o diálogo inteiro
            // e produz exatamente as falas fora de ordem que o servidor evita.
            OffsetAtendenteMs = 0,
            OffsetClienteMs = 0,
            // Dado do cadastro vence a transcrição no servidor: o Whisper erra mais
            // em dígito ditado do que o cartão do solicitante erra.
            Cadastro = temCadastro
                ? new CadastroCliente
                {
                    Nome = m.NomeCliente,
                    Email = m.EmailCliente,
                    Telefone = m.TelefoneCliente,
                }
                : null,
        };
    }

    private async Task<RespostaEnvio> PostarAsync(
        string wavAtendente, string wavCliente, MetadadosLigacao metadados, CancellationToken ct)
    {
        using var conteudo = new MultipartFormDataContent();

        var atendente = new StreamContent(File.OpenRead(wavAtendente));
        var cliente = new StreamContent(File.OpenRead(wavCliente));
        atendente.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        cliente.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");

        conteudo.Add(atendente, "agente", Path.GetFileName(wavAtendente));
        conteudo.Add(cliente, "cliente", Path.GetFileName(wavCliente));
        conteudo.Add(new StringContent(JsonSerializer.Serialize(metadados, Json), Encoding.UTF8),
                     "metadata");

        using var resposta = await _http
            .PostAsync($"{_cfg.Url.TrimEnd('/')}/v1/calls", conteudo, ct).ConfigureAwait(false);
        var corpo = await resposta.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        // 401 e 422 são recusas definitivas: reenviar idêntico falha de novo, então
        // não entram na fila de pendentes — ficariam tentando para sempre.
        if ((int)resposta.StatusCode is 401 or 422)
            throw new EnvioRecusadoException($"HTTP {(int)resposta.StatusCode}: {corpo}");

        resposta.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<RespostaEnvio>(corpo, Json)
               ?? throw new EnvioRecusadoException($"resposta inesperada: {corpo}");
    }

    // --- fila local -------------------------------------------------------

    private string Reter(AudioCapture captura, MetadadosLigacao metadados)
    {
        var pasta = Path.Combine(_pendentes, $"{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}"[..24]);
        Directory.CreateDirectory(pasta);

        File.Copy(captura.CaminhoAtendente, Path.Combine(pasta, "agente.wav"), overwrite: true);
        File.Copy(captura.CaminhoCliente, Path.Combine(pasta, "cliente.wav"), overwrite: true);
        File.WriteAllText(Path.Combine(pasta, "metadata.json"),
                          JsonSerializer.Serialize(metadados, Json), Encoding.UTF8);
        return pasta;
    }

    /// <summary>Sobe o que ficou para trás. Chamado por timer e na abertura do app.</summary>
    public async Task DrenarPendentesAsync(CancellationToken ct = default)
    {
        // Uma drenagem por vez: duas em paralelo enviariam a mesma pasta duas vezes
        // e criariam ligação duplicada no servidor.
        if (!await _umDeCadaVez.WaitAsync(0, ct).ConfigureAwait(false))
            return;

        try
        {
            foreach (var pasta in Directory.EnumerateDirectories(_pendentes).OrderBy(p => p))
            {
                ct.ThrowIfCancellationRequested();

                var agente = Path.Combine(pasta, "agente.wav");
                var cliente = Path.Combine(pasta, "cliente.wav");
                var meta = Path.Combine(pasta, "metadata.json");
                if (!File.Exists(agente) || !File.Exists(cliente) || !File.Exists(meta))
                    continue;

                var contador = Path.Combine(pasta, "tentativas");
                var tentativas = File.Exists(contador) && int.TryParse(File.ReadAllText(contador), out var n)
                    ? n : 0;
                if (tentativas >= _cfg.MaxTentativas)
                    continue;  // desiste de tentar, mas NÃO apaga: fica para inspeção

                try
                {
                    var metadados = JsonSerializer.Deserialize<MetadadosLigacao>(
                        File.ReadAllText(meta), Json)!;
                    var resposta = await PostarAsync(agente, cliente, metadados, ct).ConfigureAwait(false);

                    Directory.Delete(pasta, recursive: true);
                    _log.LogInformation("Pendente enviada: {CallId}", resposta.CallId);
                    LigacaoAceita?.Invoke(this, resposta);
                }
                catch (EnvioRecusadoException e)
                {
                    File.WriteAllText(contador, _cfg.MaxTentativas.ToString());
                    _log.LogError("Pendente {Pasta} recusada em definitivo: {Erro}",
                                  Path.GetFileName(pasta), e.Message);
                }
                catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
                {
                    File.WriteAllText(contador, (tentativas + 1).ToString());
                    break;  // servidor ainda fora: não insiste nas outras agora
                }
            }
        }
        finally
        {
            _umDeCadaVez.Release();
        }
    }

    public int PendentesEmDisco() =>
        Directory.Exists(_pendentes) ? Directory.EnumerateDirectories(_pendentes).Count() : 0;

    /// <summary>
    /// Estado de uma ligação no servidor. Devolve <c>null</c> quando o servidor não a
    /// conhece (404) — situação diferente de "ainda processando", e que precisa parar o
    /// acompanhamento em vez de repetir para sempre.
    /// </summary>
    public async Task<EstadoLigacao?> ConsultarAsync(string callId, CancellationToken ct = default)
    {
        using var resposta = await _http
            .GetAsync($"{_cfg.Url.TrimEnd('/')}/v1/calls/{Uri.EscapeDataString(callId)}", ct)
            .ConfigureAwait(false);

        if (resposta.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;

        resposta.EnsureSuccessStatusCode();
        var corpo = await resposta.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<EstadoLigacao>(corpo, Json);
    }

    /// <summary>
    /// Manda o servidor processar de novo, sem reenviar áudio — ele guardou os dois canais.
    /// É o que mantém o botão "Reprocessar" funcionando depois de um ajuste de vocabulário
    /// no servidor.
    /// </summary>
    public async Task<bool> ReprocessarAsync(string callId, CancellationToken ct = default)
    {
        using var resposta = await _http
            .PostAsync($"{_cfg.Url.TrimEnd('/')}/v1/calls/{Uri.EscapeDataString(callId)}/reprocess",
                       content: null, ct)
            .ConfigureAwait(false);
        return resposta.IsSuccessStatusCode;
    }

    /// <summary>Servidor no ar? Usado pelo indicador de estado na bandeja.</summary>
    public async Task<bool> ServidorNoArAsync(CancellationToken ct = default)
    {
        try
        {
            using var r = await _http.GetAsync($"{_cfg.Url.TrimEnd('/')}/v1/health", ct)
                .ConfigureAwait(false);
            return r.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public void Dispose()
    {
        _reenvio.Dispose();
        _http.Dispose();
        _umDeCadaVez.Dispose();
    }
}

/// <summary>Recusa definitiva do servidor. Reenviar o mesmo conteúdo não adianta.</summary>
public sealed class EnvioRecusadoException(string mensagem) : Exception(mensagem);

public sealed record MetadadosLigacao
{
    [JsonPropertyName("ticket")] public string? Ticket { get; init; }
    [JsonPropertyName("telefone")] public string? Telefone { get; init; }
    [JsonPropertyName("agent_id")] public string? AgentId { get; init; }
    [JsonPropertyName("iniciada_em")] public DateTimeOffset? IniciadaEm { get; init; }
    [JsonPropertyName("offset_agente_ms")] public int OffsetAtendenteMs { get; init; }
    [JsonPropertyName("offset_cliente_ms")] public int OffsetClienteMs { get; init; }
    [JsonPropertyName("cadastro")] public CadastroCliente? Cadastro { get; init; }
}

public sealed record CadastroCliente
{
    [JsonPropertyName("nome")] public string? Nome { get; init; }
    [JsonPropertyName("cpf")] public string? Cpf { get; init; }
    [JsonPropertyName("cnpj")] public string? Cnpj { get; init; }
    [JsonPropertyName("email")] public string? Email { get; init; }
    [JsonPropertyName("telefone")] public string? Telefone { get; init; }
}

public sealed record RespostaEnvio(
    [property: JsonPropertyName("call_id")] string CallId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("posicao")] int? Posicao,
    [property: JsonPropertyName("duracao_ms")] long DuracaoMs);
