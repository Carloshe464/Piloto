using System.Globalization;
using System.Text;

namespace Piloto.Core.Text;

public static class TextUtils
{
    /// <summary>Remove acentos/diacríticos (para comparações e busca).</summary>
    public static string RemoverAcentos(string texto)
    {
        if (string.IsNullOrEmpty(texto)) return texto;
        var normalizado = texto.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalizado.Length);
        foreach (var c in normalizado)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        }
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary>Forma canônica para comparação: sem acento, minúscula, espaços colapsados.</summary>
    public static string Canonizar(string texto)
    {
        var semAcento = RemoverAcentos(texto).ToLowerInvariant();
        return string.Join(' ', semAcento.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>Mantém apenas dígitos.</summary>
    public static string SomenteDigitos(string texto)
    {
        var sb = new StringBuilder(texto.Length);
        foreach (var c in texto)
            if (char.IsDigit(c)) sb.Append(c);
        return sb.ToString();
    }
}
