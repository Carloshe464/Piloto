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
    public CapturaSettings Captura { get; set; } = new();
    public RetencaoSettings RetencaoDias { get; set; } = new();

    /// <summary>Pode conter variáveis de ambiente no formato %VAR% (Windows).</summary>
    public string PastaDados { get; set; } = "%LOCALAPPDATA%\\Piloto";

    /// <summary>Caminho absoluto da pasta de dados, com variáveis já expandidas.</summary>
    [JsonIgnore]
    public string PastaDadosExpandida => Environment.ExpandEnvironmentVariables(PastaDados);

    /// <summary>Pasta dos modelos das versões anteriores (~2,6 GB). Existe apenas para o
    /// app apagá-la na primeira abertura depois da atualização.</summary>
    [JsonIgnore]
    public string PastaModelosLegado => Path.Combine(PastaDadosExpandida, "models");

    [JsonIgnore]
    public string PastaAudio => Path.Combine(PastaDadosExpandida, "audio");

    [JsonIgnore]
    public string CaminhoBanco => Path.Combine(PastaDadosExpandida, "piloto.db");

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

    /// <summary>
    /// Quando o endereço e o token foram aplicados a partir do arquivo deixado pelo
    /// instalador.
    /// <para>
    /// Existe porque a configuração do usuário vive em %LOCALAPPDATA% e o instalador roda
    /// elevado, possivelmente noutro perfil — ele não consegue escrever ali. O instalador
    /// grava na pasta do programa e o app aplica na primeira abertura, já como o usuário
    /// certo. Comparar com a data do arquivo é o que impede duas coisas ao mesmo tempo:
    /// reaplicar a cada abertura, sobrescrevendo o que o atendente ajustou na tela; e
    /// ignorar uma reinstalação feita para trocar o servidor.
    /// </para>
    /// </summary>
    public DateTimeOffset? ProvisionadoEm { get; set; }
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

public sealed class CapturaSettings
{
    /// <summary>
    /// Quanto esperar, depois que a ligação encerra, pelo ticket e pelo telefone antes de
    /// enviar a gravação ao servidor.
    /// <para>
    /// O ticket costuma ser aberto alguns segundos DEPOIS de a chamada cair, e o servidor
    /// grava ticket e telefone no instante em que o áudio é enfileirado — não existe forma
    /// de completá-los depois. Sem esta espera, justamente a ligação que tem ticket sobe
    /// sem ele.
    /// </para>
    /// <para>
    /// A espera termina antes do prazo assim que os dois dados chegam; o valor aqui é só o
    /// teto. Zero desliga e envia na hora, como era antes.
    /// </para>
    /// </summary>
    public int EsperaIdentificacaoSegundos { get; set; } = 15;
}

// WhisperSettings, LlmSettings e FilaSettings saíram na 1.1: modelo, threads,
// temperatura e paralelismo são decisões do servidor, e mantê-los aqui só ofereceria
// ao atendente controles que não fazem nada.

public sealed class RetencaoSettings
{
    public int Audio { get; set; } = 30;
    public int Transcricao { get; set; } = 180;
}
