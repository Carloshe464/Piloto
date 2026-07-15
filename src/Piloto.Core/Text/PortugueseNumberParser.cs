using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Piloto.Core.Text;

/// <summary>
/// Converte números falados em português para dígitos.
/// <para>
/// Trata dois modos comuns na fala de um atendimento:
/// <list type="bullet">
///   <item>Composição cardinal: "trezentos e vinte e cinco" → <c>325</c>.</item>
///   <item>Sequência de dígitos ditados: "meia nove um dois" → <c>6912</c> (telefone/CPF/protocolo).</item>
/// </list>
/// A heurística: se a sequência contém dezenas exatas, centenas ou escalas (mil, milhão),
/// é interpretada como cardinal; se contém apenas unidades/adolescentes/"meia", é concatenação de dígitos.
/// </para>
/// </summary>
public static class PortugueseNumberParser
{
    private static readonly Dictionary<string, long> Unidades = new(StringComparer.OrdinalIgnoreCase)
    {
        ["zero"] = 0, ["um"] = 1, ["uma"] = 1, ["dois"] = 2, ["duas"] = 2,
        ["tres"] = 3, ["três"] = 3, ["quatro"] = 4, ["cinco"] = 5,
        ["seis"] = 6, ["meia"] = 6, ["sete"] = 7, ["oito"] = 8, ["nove"] = 9,
    };

    private static readonly Dictionary<string, long> Adolescentes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["dez"] = 10, ["onze"] = 11, ["doze"] = 12, ["treze"] = 13,
        ["quatorze"] = 14, ["catorze"] = 14, ["quinze"] = 15, ["dezesseis"] = 16,
        ["dezasseis"] = 16, ["dezessete"] = 17, ["dezassete"] = 17, ["dezoito"] = 18,
        ["dezenove"] = 19, ["dezanove"] = 19,
    };

    private static readonly Dictionary<string, long> Dezenas = new(StringComparer.OrdinalIgnoreCase)
    {
        ["vinte"] = 20, ["trinta"] = 30, ["quarenta"] = 40, ["cinquenta"] = 50,
        ["cincoenta"] = 50, ["sessenta"] = 60, ["setenta"] = 70, ["oitenta"] = 80, ["noventa"] = 90,
    };

    private static readonly Dictionary<string, long> Centenas = new(StringComparer.OrdinalIgnoreCase)
    {
        ["cem"] = 100, ["cento"] = 100,
        ["duzentos"] = 200, ["duzentas"] = 200, ["trezentos"] = 300, ["trezentas"] = 300,
        ["quatrocentos"] = 400, ["quatrocentas"] = 400, ["quinhentos"] = 500, ["quinhentas"] = 500,
        ["seiscentos"] = 600, ["seiscentas"] = 600, ["setecentos"] = 700, ["setecentas"] = 700,
        ["oitocentos"] = 800, ["oitocentas"] = 800, ["novecentos"] = 900, ["novecentas"] = 900,
    };

    private static readonly Dictionary<string, long> Escalas = new(StringComparer.OrdinalIgnoreCase)
    {
        ["mil"] = 1_000,
        ["milhao"] = 1_000_000, ["milhão"] = 1_000_000, ["milhoes"] = 1_000_000, ["milhões"] = 1_000_000,
        ["bilhao"] = 1_000_000_000, ["bilhão"] = 1_000_000_000, ["bilhoes"] = 1_000_000_000, ["bilhões"] = 1_000_000_000,
    };

    private static readonly Regex RunRegex = BuildRunRegex();

    private static Regex BuildRunRegex()
    {
        var palavras = Unidades.Keys
            .Concat(Adolescentes.Keys)
            .Concat(Dezenas.Keys)
            .Concat(Centenas.Keys)
            .Concat(Escalas.Keys)
            .Distinct()
            .OrderByDescending(w => w.Length) // mais longas primeiro: "dezessete" antes de "dez"
            .Select(Regex.Escape);

        var alternacao = string.Join('|', palavras);
        // Uma "palavra numérica" seguida de zero ou mais, separadas por espaço e opcionalmente "e".
        var padrao = $@"\b(?:{alternacao})\b(?:\s+(?:e\s+)?\b(?:{alternacao})\b)*";
        return new Regex(padrao, RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }

    /// <summary>Substitui todas as sequências de números falados por dígitos no texto.</summary>
    public static string Converter(string texto)
    {
        if (string.IsNullOrWhiteSpace(texto)) return texto;
        return RunRegex.Replace(texto, m => ConverterRun(m.Value));
    }

    /// <summary>Converte um único trecho já identificado como sequência numérica.</summary>
    public static string ConverterRun(string run)
    {
        var tokens = run
            .Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(t => !t.Equals("e", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (tokens.Count == 0) return run;

        bool ehCardinal = tokens.Any(t =>
            Dezenas.ContainsKey(t) || Centenas.ContainsKey(t) || Escalas.ContainsKey(t));

        return ehCardinal
            ? ParseCardinal(tokens).ToString(CultureInfo.InvariantCulture)
            : ConcatenarDigitos(tokens);
    }

    private static string ConcatenarDigitos(IEnumerable<string> tokens)
    {
        var sb = new StringBuilder();
        foreach (var t in tokens)
        {
            if (Unidades.TryGetValue(t, out var u))
                sb.Append(u.ToString(CultureInfo.InvariantCulture));
            else if (Adolescentes.TryGetValue(t, out var a))
                sb.Append(a.ToString(CultureInfo.InvariantCulture));
        }
        return sb.Length > 0 ? sb.ToString() : string.Join(' ', tokens);
    }

    private static long ParseCardinal(IEnumerable<string> tokens)
    {
        long resultado = 0;
        long atual = 0;

        foreach (var t in tokens)
        {
            if (Escalas.TryGetValue(t, out var escala))
            {
                if (atual == 0) atual = 1;
                atual *= escala;
                resultado += atual;
                atual = 0;
            }
            else if (Valor(t, out var v))
            {
                atual += v;
            }
        }

        return resultado + atual;
    }

    private static bool Valor(string token, out long valor)
    {
        if (Unidades.TryGetValue(token, out valor)) return true;
        if (Adolescentes.TryGetValue(token, out valor)) return true;
        if (Dezenas.TryGetValue(token, out valor)) return true;
        if (Centenas.TryGetValue(token, out valor)) return true;
        valor = 0;
        return false;
    }
}
