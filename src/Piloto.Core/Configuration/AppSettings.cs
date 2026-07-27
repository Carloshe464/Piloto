using System.Text.Json;
using System.Text.Json.Serialization;

namespace Piloto.Core.Configuration;

/// <summary>
/// Reflete <c>config/appsettings.json</c>. Os nomes em português seguem o arquivo
/// de configuração do produto para que o administrador edite sem tradução mental.
/// </summary>
public sealed class AppSettings
{
    public BridgeSettings Bridge { get; set; } = new();
    public AudioSettings Audio { get; set; } = new();
    public ServidorSettings Servidor { get; set; } = new();
    public LlmSettings Llm { get; set; } = new();
    public FilaSettings Fila { get; set; } = new();
    public RetencaoSettings RetencaoDias { get; set; } = new();

    /// <summary>Pode conter variáveis de ambiente no formato %VAR% (Windows).</summary>
    public string PastaDados { get; set; } = "%LOCALAPPDATA%\\Piloto";

    /// <summary>Caminho absoluto da pasta de dados, com variáveis já expandidas.</summary>
    [JsonIgnore]
    public string PastaDadosExpandida => Environment.ExpandEnvironmentVariables(PastaDados);

    [JsonIgnore]
    public string PastaModelos => Path.Combine(PastaDadosExpandida, "models");

    [JsonIgnore]
    public string PastaAudio => Path.Combine(PastaDadosExpandida, "audio");

    [JsonIgnore]
    public string CaminhoBanco => Path.Combine(PastaDadosExpandida, "piloto.db");

    [JsonIgnore]
    public string CaminhoModeloLlm => Path.Combine(PastaModelos, Llm.Modelo);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static AppSettings Load(string caminho)
    {
        if (!File.Exists(caminho))
            return new AppSettings();

        var json = File.ReadAllText(caminho);
        return JsonSerializer.Deserialize<AppSettings>(json, JsonOpts) ?? new AppSettings();
    }

    public void Save(string caminho)
    {
        var dir = Path.GetDirectoryName(caminho);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
        File.WriteAllText(caminho, json);
    }
}

public sealed class BridgeSettings
{
    public int Porta { get; set; } = 8517;
}

public sealed class AudioSettings
{
    public string ProcessoNavegador { get; set; } = "chrome";
    public string Formato { get; set; } = "wav";
    public int TaxaHz { get; set; } = 16000;
}

/// <summary>
/// Servidor de transcrição. O trabalho pesado (Whisper, e mais adiante a análise e o
/// resumo) roda lá; o piloto envia os dois canais e exibe o que volta.
/// <para>
/// A URL e o token são <b>configuração</b>, nunca constante compilada: um token por
/// máquina permite revogar uma sem mexer nas outras.
/// </para>
/// </summary>
public sealed class ServidorSettings
{
    public string Url { get; set; } = "http://DESKTOP-VEP5JQ3:8600";

    /// <summary>Bearer token (<c>CW_TOKENS</c> no servidor). Vazio = servidor sem autenticação.</summary>
    public string Token { get; set; } = "";

    /// <summary>Teto de uma requisição HTTP. Precisa ser maior que os 120 s do long-poll —
    /// e folgado o bastante para o upload de uma ligação longa (90 min ≈ 170 MB).</summary>
    public int TimeoutSegundos { get; set; } = 300;

    /// <summary>Tentativas antes de a ligação ir para revisão. Falha de rede <b>não</b>
    /// consome tentativa (ver <c>QueueProcessor</c>): só recusa do servidor e erro de
    /// processamento contam.</summary>
    public int MaxTentativas { get; set; } = 3;
}

public sealed class LlmSettings
{
    public bool Habilitado { get; set; } = true;
    public string Modelo { get; set; } = "gemma-3-4b-it-Q4_K_M.gguf";
    public float Temperatura { get; set; } = 0f;

    /// <summary>0 = automático (metade dos threads lógicos da máquina, entre 2 e 8).</summary>
    public int Threads { get; set; } = 0;

    public int Contexto { get; set; } = 4096;

    /// <summary>Força a saída JSON por gramática GBNF. Válvula de escape: desligue se a
    /// gramática causar problema em campo — o parser tolerante + grounding seguram o resto.</summary>
    public bool Gramatica { get; set; } = true;
}

public sealed class FilaSettings
{
    /// <summary>Uma ligação por vez. Existia porque duas passadas de Whisper na mesma
    /// máquina se atropelavam; com o peso no servidor daria para subir, mas a idempotência
    /// ainda não foi exercitada sob concorrência — sobe depois de campo, não antes.</summary>
    public int Simultaneas { get; set; } = 1;
}

public sealed class RetencaoSettings
{
    public int Audio { get; set; } = 30;
    public int Transcricao { get; set; } = 180;
}
