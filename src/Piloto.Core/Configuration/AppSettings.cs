using System.Text.Json;
using System.Text.Json.Serialization;

namespace Piloto.Core.Configuration;

/// <summary>
/// Reflete <c>config/appsettings.json</c>. Os nomes em português seguem o arquivo
/// de configuração do produto para que o administrador edite sem tradução mental.
/// </summary>
public sealed class AppSettings
{
    public ServidorSettings Servidor { get; set; } = new();
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

/// <summary>Servidor de transcrição. É para lá que a gravação vai; nada é processado aqui.</summary>
public sealed class ServidorSettings
{
    /// <summary>
    /// Endereço do servidor. <b>Atenção à porta:</b> a bridge da extensão também usa 8517
    /// nesta máquina. Se o servidor rodar no mesmo computador que o aplicativo — só faz
    /// sentido em desenvolvimento — uma das duas portas tem de mudar, senão a bridge não
    /// sobe e a extensão para de mandar ticket e telefone.
    /// </summary>
    public string Url { get; set; } = "http://servidor:8517";

    /// <summary>Token do agente. Vazio só funciona se o servidor também estiver sem token.</summary>
    public string Token { get; set; } = "";

    /// <summary>Upload de dezenas de MB por rede interna: generoso de propósito.</summary>
    public int TimeoutSegundos { get; set; } = 300;

    /// <summary>De quanto em quanto tempo tenta subir o que ficou retido em disco.</summary>
    public int IntervaloReenvioSegundos { get; set; } = 60;

    /// <summary>
    /// Depois disso a gravação para de ser reenviada — mas NÃO é apagada. Fica em
    /// %LOCALAPPDATA%\Piloto\pendentes para alguém olhar. Apagar automaticamente
    /// transformaria um problema de rede em ligação perdida sem rastro.
    /// </summary>
    public int MaxTentativas { get; set; } = 10;

    /// <summary>De quanto em quanto tempo pergunta ao servidor se o resultado ficou pronto.</summary>
    public int IntervaloConsultaSegundos { get; set; } = 10;
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

    /// <summary>0 = automático (metade dos threads lógicos da máquina, entre 2 e 8).</summary>
    public int Threads { get; set; } = 0;
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
    public int Simultaneas { get; set; } = 1;
    public string PrioridadeProcesso { get; set; } = "BelowNormal";
}

public sealed class RetencaoSettings
{
    public int Audio { get; set; } = 30;
    public int Transcricao { get; set; } = 180;
}
