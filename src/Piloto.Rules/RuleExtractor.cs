using System.Text.RegularExpressions;
using Piloto.Core.Abstractions;
using Piloto.Core.Models;
using Piloto.Core.Text;

namespace Piloto.Rules;

/// <summary>
/// Camada 1 — REGRAS. Extrai telefone, CPF, e-mail, datas, valores e protocolo do texto
/// já normalizado, atribuindo confiança a cada detecção. Trechos já classificados são
/// "consumidos" para evitar que o mesmo número vire dois campos.
/// </summary>
public sealed class RuleExtractor : IRuleExtractor
{
    private readonly ITextNormalizer _normalizer;

    public RuleExtractor(ITextNormalizer normalizer) => _normalizer = normalizer;

    private static readonly Regex ReEmail = new(
        @"[\w.+-]+@[\w-]+\.[\w.-]+\w", RegexOptions.Compiled);

    private static readonly Regex ReValorReais = new(
        @"R\$\s?\d{1,3}(?:\.\d{3})*(?:,\d{2})?", RegexOptions.Compiled);
    private static readonly Regex ReValorPorExtenso = new(
        @"\b\d+(?:[.,]\d{1,2})?\s?reais\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ReDataNumerica = new(
        @"\b(\d{1,2})/(\d{1,2})(?:/(\d{2,4}))?\b", RegexOptions.Compiled);
    private static readonly Regex ReDataExtenso = new(
        @"\b(\d{1,2})\s+de\s+(janeiro|fevereiro|mar[çc]o|abril|maio|junho|julho|agosto|setembro|outubro|novembro|dezembro)(?:\s+de\s+(\d{2,4}))?\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ReCpf = new(
        @"\b(\d{3})[.\s]?(\d{3})[.\s]?(\d{3})[-\s]?(\d{2})\b", RegexOptions.Compiled);

    private static readonly Regex ReTelefoneFormatado = new(
        @"\(?\b(\d{2})\)?[\s-]?(9?\d{4})[\s-]?(\d{4})\b", RegexOptions.Compiled);
    private static readonly Regex ReTelefoneBruto = new(
        @"\b(\d{10,11})\b", RegexOptions.Compiled);

    private static readonly Regex ReProtocolo = new(
        @"\bprotocolo\s*(?:n[uú]mero|n[º°\.]|:)?\s*([\d][\d.\-/]{5,})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ReDigitosLongos = new(
        @"\b\d{8,}\b", RegexOptions.Compiled);

    public ObjectiveFields Extrair(Transcript transcript)
    {
        var campos = new ObjectiveFields();
        if (transcript.EstaVazio) return campos;

        var texto = _normalizer.Normalizar(transcript.TextoCorrido());
        var consumidos = new List<(int Ini, int Fim)>();

        bool Livre(Match m)
        {
            var ini = m.Index;
            var fim = m.Index + m.Length;
            foreach (var (rIni, rFim) in consumidos)
                if (ini < rFim && fim > rIni) return false;
            return true;
        }

        void Consumir(Match m) => consumidos.Add((m.Index, m.Index + m.Length));

        // 1) E-mails
        foreach (Match m in ReEmail.Matches(texto))
        {
            if (!Livre(m)) continue;
            campos.Emails.Add(new ExtractedValue
            {
                Tipo = FieldType.Email,
                Valor = m.Value.ToLowerInvariant(),
                TrechoOrigem = m.Value,
                Confianca = 0.9,
            });
            Consumir(m);
        }

        // 2) Valores (R$ antes de tudo para não confundir dígitos com telefone/protocolo)
        foreach (Match m in ReValorReais.Matches(texto))
        {
            if (!Livre(m)) continue;
            campos.Valores.Add(new ExtractedValue
            {
                Tipo = FieldType.Valor,
                Valor = m.Value.Trim(),
                TrechoOrigem = m.Value.Trim(),
                Confianca = 0.9,
            });
            Consumir(m);
        }
        foreach (Match m in ReValorPorExtenso.Matches(texto))
        {
            if (!Livre(m)) continue;
            campos.Valores.Add(new ExtractedValue
            {
                Tipo = FieldType.Valor,
                Valor = m.Value.Trim(),
                TrechoOrigem = m.Value.Trim(),
                Confianca = 0.75,
            });
            Consumir(m);
        }

        // 3) Datas
        foreach (Match m in ReDataNumerica.Matches(texto))
        {
            if (!Livre(m)) continue;
            if (!DataNumericaPlausivel(m)) continue;
            campos.Datas.Add(new ExtractedValue
            {
                Tipo = FieldType.Data,
                Valor = m.Value,
                TrechoOrigem = m.Value,
                Confianca = 0.9,
            });
            Consumir(m);
        }
        foreach (Match m in ReDataExtenso.Matches(texto))
        {
            if (!Livre(m)) continue;
            campos.Datas.Add(new ExtractedValue
            {
                Tipo = FieldType.Data,
                Valor = m.Value.Trim(),
                TrechoOrigem = m.Value.Trim(),
                Confianca = 0.85,
            });
            Consumir(m);
        }

        // 4) Protocolo com rótulo explícito (alta prioridade: vence telefone/CPF de mesma sequência)
        foreach (Match m in ReProtocolo.Matches(texto))
        {
            if (!Livre(m)) continue;
            campos.Protocolos.Add(new ExtractedValue
            {
                Tipo = FieldType.Protocolo,
                Valor = m.Groups[1].Value.Trim(),
                TrechoOrigem = m.Value.Trim(),
                Confianca = 0.9,
            });
            Consumir(m);
        }

        // 5) CPF (com separadores sempre; 11 dígitos crus só se passar no verificador)
        foreach (Match m in ReCpf.Matches(texto))
        {
            if (!Livre(m)) continue;
            var digitos = TextUtils.SomenteDigitos(m.Value);
            var temSeparador = m.Value.Contains('.') || m.Value.Contains('-');
            var valido = Validators.CpfValido(digitos);
            if (!temSeparador && !valido) continue; // deixa para telefone

            campos.Cpfs.Add(new ExtractedValue
            {
                Tipo = FieldType.Cpf,
                Valor = FormatarCpf(digitos),
                TrechoOrigem = m.Value,
                Confianca = valido ? 0.95 : 0.45,
            });
            Consumir(m);
        }

        // 6) Telefone (formatado com DDD e, depois, sequências cruas de 10-11 dígitos)
        foreach (Match m in ReTelefoneFormatado.Matches(texto))
        {
            if (!Livre(m)) continue;
            var digitos = TextUtils.SomenteDigitos(m.Value);
            if (digitos.Length is < 10 or > 11) continue;
            campos.Telefones.Add(NovoTelefone(digitos, m.Value, digitos.Length == 11 ? 0.9 : 0.85));
            Consumir(m);
        }
        foreach (Match m in ReTelefoneBruto.Matches(texto))
        {
            if (!Livre(m)) continue;
            var digitos = m.Groups[1].Value;
            campos.Telefones.Add(NovoTelefone(digitos, m.Value, 0.7));
            Consumir(m);
        }

        // 7) Protocolo por dígitos longos remanescentes (baixa confiança)
        foreach (Match m in ReDigitosLongos.Matches(texto))
        {
            if (!Livre(m)) continue;
            campos.Protocolos.Add(new ExtractedValue
            {
                Tipo = FieldType.Protocolo,
                Valor = m.Value,
                TrechoOrigem = m.Value,
                Confianca = 0.5,
            });
            Consumir(m);
        }

        return campos;
    }

    private static ExtractedValue NovoTelefone(string digitos, string trecho, double confianca) => new()
    {
        Tipo = FieldType.Telefone,
        Valor = digitos,
        TrechoOrigem = trecho.Trim(),
        Confianca = confianca,
    };

    private static bool DataNumericaPlausivel(Match m)
    {
        var dia = int.Parse(m.Groups[1].Value);
        var mes = int.Parse(m.Groups[2].Value);
        return dia is >= 1 and <= 31 && mes is >= 1 and <= 12;
    }

    private static string FormatarCpf(string d)
        => d.Length == 11 ? $"{d[..3]}.{d.Substring(3, 3)}.{d.Substring(6, 3)}-{d.Substring(9, 2)}" : d;
}
