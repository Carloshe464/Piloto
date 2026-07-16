using Piloto.Core.Abstractions;
using Piloto.Core.Configuration;

namespace Piloto.Core.Services;

/// <summary>Verifica a presença dos modelos em disco a cada consulta (permite baixar em runtime).</summary>
public sealed class ModelCatalog : IModelCatalog
{
    private readonly AppSettings _settings;

    public ModelCatalog(AppSettings settings) => _settings = settings;

    public bool WhisperDisponivel => CandidatosWhisper.Count > 0;

    public bool LlmDisponivel => CandidatosLlm.Count > 0;

    /// <summary>
    /// O pipeline pode rodar se o Whisper existe. O LLM é opcional: quando desligado no
    /// config (8 GB RAM), o registro sai sem resumo, mas o restante do pipeline funciona.
    /// </summary>
    public bool PipelinePronto =>
        WhisperDisponivel && (!_settings.Llm.Habilitado || LlmDisponivel);

    public string? CaminhoWhisper => CandidatosWhisper.FirstOrDefault();

    public IReadOnlyList<string> CandidatosWhisper
    {
        get
        {
            var configurado = _settings.CaminhoModeloWhisper;
            var lista = new List<string>();
            if (File.Exists(configurado))
                lista.Add(configurado);

            var pasta = Path.GetDirectoryName(configurado);
            if (!string.IsNullOrEmpty(pasta) && Directory.Exists(pasta))
            {
                lista.AddRange(Directory.EnumerateFiles(pasta, "*.bin")
                    .Where(f => !string.Equals(f, configurado, StringComparison.OrdinalIgnoreCase)));
            }
            // No Whisper, maior = melhor: a ordem final é sempre por tamanho.
            return lista.OrderByDescending(f => new FileInfo(f).Length).ToList();
        }
    }

    public string? CaminhoLlm => CandidatosLlm.FirstOrDefault();

    public IReadOnlyList<string> CandidatosLlm
    {
        get
        {
            var configurado = _settings.CaminhoModeloLlm;
            var lista = new List<string>();
            if (File.Exists(configurado))
                lista.Add(configurado);

            var pasta = Path.GetDirectoryName(configurado);
            if (!string.IsNullOrEmpty(pasta) && Directory.Exists(pasta))
            {
                lista.AddRange(Directory.EnumerateFiles(pasta, "*.gguf")
                    .Where(f => !string.Equals(f, configurado, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(f => new FileInfo(f).Length));
            }
            return lista;
        }
    }

    public IReadOnlyList<string> ModelosAusentes()
    {
        var faltando = new List<string>();
        if (!WhisperDisponivel)
            faltando.Add($"Whisper: {_settings.Whisper.Modelo}");
        if (_settings.Llm.Habilitado && !LlmDisponivel)
            faltando.Add($"LLM: {_settings.Llm.Modelo}");
        return faltando;
    }
}
