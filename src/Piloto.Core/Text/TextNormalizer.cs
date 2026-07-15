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

    public string Normalizar(string texto)
    {
        if (string.IsNullOrWhiteSpace(texto)) return texto ?? string.Empty;

        var t = texto;

        // "fulano arroba dominio ponto com" → "fulano@dominio.com"
        t = Arroba.Replace(t, "@");
        t = PontoEmail.Replace(t, ".$1");

        // Números por extenso → dígitos
        t = PortugueseNumberParser.Converter(t);

        // Colapsa espaços
        t = EspacosMultiplos.Replace(t, " ");

        return t.Trim();
    }
}
