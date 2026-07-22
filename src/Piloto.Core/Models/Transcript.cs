using System.Text;

namespace Piloto.Core.Models;

/// <summary>Limites do resumo automático compartilhados entre o pipeline (que avisa o
/// humano) e o PromptBuilder (que aplica o corte) — um número só, nunca dois.</summary>
public static class ResumoLimites
{
    /// <summary>Diálogo acima disto é cortado (início + fim) antes de ir ao LLM:
    /// contexto de 4096 tokens ≈ 10.000 caracteres úteis em PT.</summary>
    public const int MaxCharsDialogo = 10_000;
}

/// <summary>Um trecho falado, já rotulado com o interlocutor e sua janela de tempo.</summary>
public sealed class TranscriptSegment
{
    /// <summary>Abaixo desta confiança o trecho é exibido com aviso: transcrito, mas incerto.</summary>
    public const double LimiarBaixaConfianca = 0.55;

    public required Speaker Speaker { get; init; }
    public required TimeSpan Inicio { get; init; }
    public required TimeSpan Fim { get; init; }
    public required string Texto { get; init; }

    /// <summary>Probabilidade média do decodificador (0..1); null em registros antigos
    /// ou quando a biblioteca não a calculou.</summary>
    public double? Confianca { get; init; }

    public bool ConfiancaBaixa => Confianca is > 0 and < LimiarBaixaConfianca;

    public override string ToString() => $"[{Speaker.Rotulo()}] {Texto}";
}

/// <summary>
/// Diálogo completo da ligação, resultado da fusão dos dois canais ordenada por tempo.
/// </summary>
public sealed class Transcript
{
    public IReadOnlyList<TranscriptSegment> Segmentos { get; }

    public Transcript(IEnumerable<TranscriptSegment> segmentos)
    {
        Segmentos = segmentos
            .OrderBy(s => s.Inicio)
            .ThenBy(s => s.Fim)
            .ToList();
    }

    public static Transcript Vazio() => new(Array.Empty<TranscriptSegment>());

    /// <summary>Diálogo rotulado, uma fala por linha: <c>[Atendente] ...</c>.
    /// <paramref name="marcarBaixaConfianca"/> anexa um aviso aos trechos incertos —
    /// usado na exibição/exportação, NUNCA no prompt do LLM (poluiria a entrada).</summary>
    public string TextoRotulado(bool marcarBaixaConfianca = false)
    {
        var sb = new StringBuilder();
        foreach (var seg in Segmentos)
        {
            sb.Append('[').Append(seg.Speaker.Rotulo()).Append("] ").Append(seg.Texto.Trim());
            if (marcarBaixaConfianca && seg.ConfiancaBaixa)
                sb.Append(" (⚠ trecho incerto)");
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>Somente o texto falado, sem rótulos — usado nas regras e no grounding.</summary>
    public string TextoCorrido()
        => string.Join(' ', Segmentos.Select(s => s.Texto.Trim()));

    public TimeSpan TempoTotalFalado()
    {
        var total = TimeSpan.Zero;
        foreach (var s in Segmentos)
            total += (s.Fim - s.Inicio);
        return total;
    }

    public bool EstaVazio => Segmentos.Count == 0;
}
