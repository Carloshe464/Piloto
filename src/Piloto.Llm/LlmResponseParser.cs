using System.Text.Json;
using System.Text.Json.Serialization;
using Piloto.Core.Models;

namespace Piloto.Llm;

/// <summary>Extrai e interpreta o JSON devolvido pelo LLM, de forma tolerante.</summary>
public static class LlmResponseParser
{
    private sealed class Dto
    {
        [JsonPropertyName("resumo")] public string? Resumo { get; set; }
        [JsonPropertyName("motivo_contato")] public string? MotivoContato { get; set; }
        [JsonPropertyName("produto")] public string? Produto { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("pedido")] public string? Pedido { get; set; }
        [JsonPropertyName("proximo_passo")] public string? ProximoPasso { get; set; }
    }

    private static readonly JsonSerializerOptions Opts = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
    };

    public static LlmSummary Parse(string? saidaBruta)
    {
        if (string.IsNullOrWhiteSpace(saidaBruta))
            return LlmSummary.Vazio();

        var json = ExtrairJson(saidaBruta);
        if (json is null)
            return LlmSummary.Vazio();

        try
        {
            var dto = JsonSerializer.Deserialize<Dto>(json, Opts);
            if (dto is null) return LlmSummary.Vazio();
            return new LlmSummary
            {
                Resumo = Limpar(dto.Resumo),
                MotivoContato = Limpar(dto.MotivoContato),
                Produto = Limpar(dto.Produto),
                Status = Limpar(dto.Status),
                Pedido = Limpar(dto.Pedido),
                ProximoPasso = Limpar(dto.ProximoPasso),
            };
        }
        catch (JsonException)
        {
            return LlmSummary.Vazio();
        }
    }

    /// <summary>Isola o primeiro objeto JSON balanceado da saída.</summary>
    internal static string? ExtrairJson(string texto)
    {
        var ini = texto.IndexOf('{');
        if (ini < 0) return null;

        var profundidade = 0;
        var emString = false;
        var escape = false;
        for (var i = ini; i < texto.Length; i++)
        {
            var c = texto[i];
            if (emString)
            {
                if (escape) escape = false;
                else if (c == '\\') escape = true;
                else if (c == '"') emString = false;
            }
            else
            {
                if (c == '"') emString = true;
                else if (c == '{') profundidade++;
                else if (c == '}')
                {
                    profundidade--;
                    if (profundidade == 0)
                        return texto.Substring(ini, i - ini + 1);
                }
            }
        }
        return null;
    }

    private static string? Limpar(string? valor)
    {
        if (string.IsNullOrWhiteSpace(valor)) return null;
        var v = valor.Trim();
        return v.Equals("null", StringComparison.OrdinalIgnoreCase) ? null : v;
    }
}
