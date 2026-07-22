namespace Piloto.Core.Models;

/// <summary>
/// Saneamento dos segmentos de UM canal antes da fusão do diálogo.
/// <list type="bullet">
///   <item><b>Timestamps esticados:</b> o Whisper infla os tempos em canais com música/
///   silêncio, e um segmento "aos 130 s" de um áudio de 80 s embaralha a intercalação
///   por tempo (resposta antes da pergunta — registros 29 e 34) e infla o TempoFalado.
///   A compressão linear traz o canal de volta ao teto da duração real do áudio.</item>
///   <item><b>Loop de repetição:</b> sobre música/ruído o Whisper também repete a mesma
///   frase inocente em série ("Obrigado." ×20) com confiança alta — o filtro de padrão
///   não pega texto normal; a série idêntica consecutiva pega.</item>
/// </list>
/// </summary>
public static class TranscriptSanitizer
{
    /// <summary>Tolerância antes de comprimir: pequenos estouros (arredondamento da última
    /// janela do Whisper) são normais e não justificam mexer nos tempos.</summary>
    private static readonly TimeSpan ToleranciaFim = TimeSpan.FromSeconds(2);

    /// <summary>A partir desta quantidade de segmentos idênticos consecutivos é loop de
    /// alucinação, não fala real ("alô? alô?" cabe num segmento só; três iguais não).</summary>
    private const int MinRepeticoesParaLoop = 3;

    /// <summary>
    /// Comprime linearmente os timestamps do canal quando ultrapassam a duração real do
    /// áudio. Devolve o fator aplicado (&lt;1) ou null quando nada foi alterado.
    /// </summary>
    public static double? ComprimirTimestamps(List<TranscriptSegment> segmentos, TimeSpan duracaoAudio)
    {
        if (segmentos.Count == 0 || duracaoAudio <= TimeSpan.Zero) return null;

        var fimMax = segmentos.Max(s => s.Fim);
        if (fimMax <= duracaoAudio + ToleranciaFim) return null;

        var fator = duracaoAudio.TotalSeconds / fimMax.TotalSeconds;
        for (var i = 0; i < segmentos.Count; i++)
        {
            var s = segmentos[i];
            segmentos[i] = new TranscriptSegment
            {
                Speaker = s.Speaker,
                Inicio = TimeSpan.FromSeconds(s.Inicio.TotalSeconds * fator),
                Fim = TimeSpan.FromSeconds(s.Fim.TotalSeconds * fator),
                Texto = s.Texto,
                Confianca = s.Confianca,
            };
        }
        return fator;
    }

    /// <summary>
    /// Colapsa séries de 3+ segmentos consecutivos com o mesmo texto (ignorando caixa e
    /// pontuação nas bordas) em um único segmento. Devolve quantos foram removidos.
    /// </summary>
    public static int ColapsarRepeticoes(List<TranscriptSegment> segmentos)
    {
        if (segmentos.Count < MinRepeticoesParaLoop) return 0;

        static string Chave(TranscriptSegment s) => s.Texto.Trim().TrimEnd('.', '!', '?', ',').ToLowerInvariant();

        var saida = new List<TranscriptSegment>(segmentos.Count);
        var removidos = 0;
        var i = 0;
        while (i < segmentos.Count)
        {
            var chave = Chave(segmentos[i]);
            var fim = i + 1;
            while (fim < segmentos.Count && Chave(segmentos[fim]) == chave) fim++;

            var tamanho = fim - i;
            if (tamanho >= MinRepeticoesParaLoop && chave.Length > 0)
            {
                saida.Add(segmentos[i]); // uma instância fica: pode ser fala real que disparou o loop
                removidos += tamanho - 1;
            }
            else
            {
                for (var j = i; j < fim; j++) saida.Add(segmentos[j]);
            }
            i = fim;
        }

        if (removidos > 0)
        {
            segmentos.Clear();
            segmentos.AddRange(saida);
        }
        return removidos;
    }
}
