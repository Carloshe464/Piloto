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
    public WhisperSettings Whisper { get; set; } = new();
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
    public string CaminhoModeloWhisper => Path.Combine(PastaModelos, Whisper.Modelo);

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

public sealed class WhisperSettings
{
    public string Modelo { get; set; } = "ggml-small-q5_1.bin";
    public string Idioma { get; set; } = "pt";
    public int Threads { get; set; } = 5;
}

public sealed class LlmSettings
{
    public bool Habilitado { get; set; } = true;
    public string Modelo { get; set; } = "gemma-3-4b-it-Q4_K_M.gguf";
    public float Temperatura { get; set; } = 0f;
    public int Threads { get; set; } = 5;
    public int Contexto { get; set; } = 4096;
}

public sealed class FilaSettings
{
    public int Simultaneas { get; set; } = 1;
    public string PrioridadeProcesso { get; set; } = "BelowNormal";
}

public sealed class RetencaoSettings
{
    public int Audio { get; set; } = 30;
    public int Transcricao { get; set; } = 180;
}
