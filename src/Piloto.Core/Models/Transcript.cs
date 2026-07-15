using System.Text;

namespace Piloto.Core.Models;

/// <summary>Um trecho falado, já rotulado com o interlocutor e sua janela de tempo.</summary>
public sealed class TranscriptSegment
{
    public required Speaker Speaker { get; init; }
    public required TimeSpan Inicio { get; init; }
    public required TimeSpan Fim { get; init; }
    public required string Texto { get; init; }

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

    /// <summary>Diálogo rotulado, uma fala por linha: <c>[Atendente] ...</c>.</summary>
    public string TextoRotulado()
    {
        var sb = new StringBuilder();
        foreach (var seg in Segmentos)
            sb.Append('[').Append(seg.Speaker.Rotulo()).Append("] ").AppendLine(seg.Texto.Trim());
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
