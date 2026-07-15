using System.Text.Json;
using System.Text.Json.Serialization;

namespace Piloto.Core.Configuration;

/// <summary>
/// Listas fechadas (motivo, produto, status) configuráveis pelo administrador.
/// O LLM só pode <b>escolher</b> um valor dentro destas listas — nunca redigir um novo.
/// Reflete <c>config/listas.json</c>.
/// </summary>
public sealed class ListasFechadas
{
    [JsonPropertyName("motivo_contato")]
    public List<string> MotivoContato { get; set; } = new();

    [JsonPropertyName("produto")]
    public List<string> Produto { get; set; } = new();

    [JsonPropertyName("status")]
    public List<string> Status { get; set; } = new();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static ListasFechadas Load(string caminho)
    {
        if (!File.Exists(caminho))
            return new ListasFechadas();
        var json = File.ReadAllText(caminho);
        return JsonSerializer.Deserialize<ListasFechadas>(json, JsonOpts) ?? new ListasFechadas();
    }

    public void Save(string caminho)
    {
        var dir = Path.GetDirectoryName(caminho);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(caminho, json);
    }

    /// <summary>Verifica se um valor pertence à lista (comparação sem acento/caixa).</summary>
    public static bool Contem(IEnumerable<string> lista, string? valor)
    {
        if (string.IsNullOrWhiteSpace(valor)) return false;
        return lista.Any(x => string.Equals(
            x.Trim(), valor.Trim(), StringComparison.OrdinalIgnoreCase));
    }
}
