using System.Text.RegularExpressions;

namespace Piloto.Data.Export;

/// <summary>Mascara PII (CPF, telefone, e-mail) em textos exportados. Ligado por padrão.</summary>
public static class PiiMasker
{
    private static readonly Regex ReCpf = new(@"\b\d{3}\.\d{3}\.\d{3}-\d{2}\b", RegexOptions.Compiled);
    private static readonly Regex ReCpfBruto = new(@"\b\d{11}\b", RegexOptions.Compiled);
    private static readonly Regex ReTelefone = new(@"\b\d{10,11}\b", RegexOptions.Compiled);
    private static readonly Regex ReEmail = new(@"([\w.+-])[\w.+-]*(@[\w.-]+)", RegexOptions.Compiled);

    public static string Mascarar(string? texto)
    {
        if (string.IsNullOrEmpty(texto)) return texto ?? string.Empty;
        var t = texto;
        t = ReCpf.Replace(t, m => "***.***.***-" + m.Value[^2..]);
        t = ReEmail.Replace(t, m => m.Groups[1].Value + "***" + m.Groups[2].Value);
        t = ReTelefone.Replace(t, m => new string('*', m.Value.Length - 4) + m.Value[^4..]);
        t = ReCpfBruto.Replace(t, m => new string('*', 9) + m.Value[^2..]);
        return t;
    }
}
