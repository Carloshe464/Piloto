using Piloto.Core.Abstractions;
using Piloto.Core.Configuration;

namespace Piloto.Core.Services;

/// <summary>Verifica a presença do modelo LLM em disco a cada consulta (permite baixar em runtime).</summary>
public sealed class ModelCatalog : IModelCatalog
{
    private readonly AppSettings _settings;

    public ModelCatalog(AppSettings settings) => _settings = settings;

    public bool LlmDisponivel => CandidatosLlm.Count > 0;

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
        if (_settings.Llm.Habilitado && !LlmDisponivel)
            faltando.Add($"LLM: {_settings.Llm.Modelo}");
        return faltando;
    }
}
