using System.Text.RegularExpressions;
using Piloto.Core.Abstractions;

namespace Piloto.Core.Text;

/// <summary>
/// Normaliza o texto transcrito antes da extração por regras:
/// números falados → dígitos, formas ditadas de e-mail ("arroba"/"ponto"),
/// e limpeza de espaçamento. O texto normalizado alimenta as regras e o grounding;
/// a exibição continua usando a transcrição original.
/// </summary>
public sealed class TextNormalizer : ITextNormalizer
{
    private static readonly Regex EspacosMultiplos = new(@"[ \t]{2,}", RegexOptions.Compiled);
    private static readonly Regex Arroba = new(@"\s+arroba\s+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PontoEmail = new(@"\s+ponto\s+(com|br|net|org|gov)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // A filial "/0001-" de um CNPJ ditado ("barra zero zero zero um traço") chega do
    // Whisper como "mil contra", "1000 contra" ou "1.000 contra" (o small formata o
    // milhar com ponto). Só converte entre dígitos, que é o contexto de ditado —
    // "mil contra" em prosa comum fica intacto.
    private static readonly Regex MilContra = new(
        @"(?<=\d[\s,.]{0,3})\b(?:mil|1[.\s]?000)\s+contra\b[\s,.]*(?=\d)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // "barra" e "traço"/"hífen" ditados entre dígitos viram os separadores reais.
    private static readonly Regex BarraDitada = new(
        @"(?<=\d)[\s,]+barra[\s,]+(?=\d)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TracoDitado = new(
        @"(?<=\d)[\s,]+(?:tra[çc]o|h[ií]fen)[\s,]+(?=\d)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // O Whisper transcreve dígitos ditados como "1, 2, 3, 4" — o run de 4+ dígitos
    // soltos separados por vírgula/espaço vira uma sequência contígua ("1234").
    // Cada membro precisa ser dígito SOLTO: o guard (?!\d|[.,]\d) impede o run de
    // engolir o primeiro dígito de um número composto ("7, 1.000" para no 7).
    private static readonly Regex DigitosDitados = new(
        @"\b\d(?!\d|[.,]\d)(?:(?:\s*,\s*|\s+)\d(?!\d|[.,]\d)){3,}", RegexOptions.Compiled);

    public string Normalizar(string texto)
    {
        if (string.IsNullOrWhiteSpace(texto)) return texto ?? string.Empty;

        var t = texto;

        // "fulano arroba dominio ponto com" → "fulano@dominio.com"
        t = Arroba.Replace(t, "@");
        t = PontoEmail.Replace(t, ".$1");

        // Formas ditadas de documento: antes do parser numérico (que transformaria "mil" em 1000)
        t = MilContra.Replace(t, "0001-");
        t = BarraDitada.Replace(t, "/");
        t = TracoDitado.Replace(t, "-");

        // Números por extenso → dígitos
        t = PortugueseNumberParser.Converter(t);

        // Dígitos ditados um a um → sequência contígua
        t = DigitosDitados.Replace(t, m => string.Concat(m.Value.Where(char.IsDigit)));

        // Colapsa espaços
        t = EspacosMultiplos.Replace(t, " ");

        return t.Trim();
    }
}
