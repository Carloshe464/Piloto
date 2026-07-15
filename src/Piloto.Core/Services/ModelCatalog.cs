using Piloto.Core.Abstractions;
using Piloto.Core.Configuration;

namespace Piloto.Core.Services;

/// <summary>Verifica a presença dos modelos em disco a cada consulta (permite baixar em runtime).</summary>
public sealed class ModelCatalog : IModelCatalog
{
    private readonly AppSettings _settings;

    public ModelCatalog(AppSettings settings) => _settings = settings;

    public bool WhisperDisponivel => File.Exists(_settings.CaminhoModeloWhisper);

    public bool LlmDisponivel => File.Exists(_settings.CaminhoModeloLlm);

    /// <summary>
    /// O pipeline pode rodar se o Whisper existe. O LLM é opcional: quando desligado no
    /// config (8 GB RAM), o registro sai sem resumo, mas o restante do pipeline funciona.
    /// </summary>
    public bool PipelinePronto =>
        WhisperDisponivel && (!_settings.Llm.Habilitado || LlmDisponivel);

    public string? CaminhoWhisper => WhisperDisponivel ? _settings.CaminhoModeloWhisper : null;

    public string? CaminhoLlm => LlmDisponivel ? _settings.CaminhoModeloLlm : null;

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
