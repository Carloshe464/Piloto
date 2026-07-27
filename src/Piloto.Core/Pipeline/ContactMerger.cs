using System.Text.RegularExpressions;
using Piloto.Core.Models;

namespace Piloto.Core.Pipeline;

/// <summary>
/// Injeta nos campos objetivos o contato lido do cadastro do Zendesk pela extensão.
/// <para>
/// Motivo: e-mail e telefone ditados por voz são a pior aposta do pipeline. Um dígito
/// trocado num telefone ou uma letra a menos num e-mail passam por todas as regras — o
/// valor sai plausível, com confiança alta, e errado. Não há regex que detecte isso. O
/// cadastro do solicitante, ao contrário, é dado digitado: quando a extensão consegue
/// lê-lo, ele é a fonte da verdade e substitui o que o Whisper ouviu.
/// </para>
/// <para>
/// O que a camada de regras encontra continua valendo — um cliente frequentemente dita
/// um e-mail ou telefone DIFERENTE do cadastrado ("anota o novo número aí"). Os dois
/// convivem na lista; o do cadastro aparece primeiro, marcado.
/// </para>
/// </summary>
public static class ContactMerger
{
    /// <summary>E-mail plausível. Deliberadamente frouxo: o Zendesk já validou o formato
    /// no cadastro — aqui só se descarta lixo do DOM (rótulo, placeholder).</summary>
    private static readonly Regex ReEmail = new(
        @"^[^@\s]+@[^@\s.]+\.[^@\s]+$", RegexOptions.Compiled);

    /// <summary>
    /// Acrescenta e-mail, telefone, número do discador, nome e ticket aos campos objetivos.
    /// Idempotente: chamar duas vezes não duplica nada (a mesclagem compara o conteúdo).
    /// </summary>
    public static void Aplicar(ObjectiveFields campos, CallMetadata metadata)
    {
        if (NormalizarEmail(metadata.EmailCliente) is { } email)
            ObjectiveFields.Mesclar(campos.Emails, new ExtractedValue
            {
                Tipo = FieldType.Email,
                Valor = email,
                TrechoOrigem = "cadastro do Zendesk",
                Confianca = 1.0,
                Origem = FieldSource.Extensao,
            });

        if (NormalizarTelefone(metadata.TelefoneCliente) is { } telefone)
            ObjectiveFields.Mesclar(campos.Telefones, new ExtractedValue
            {
                Tipo = FieldType.Telefone,
                Valor = telefone,
                TrechoOrigem = "cadastro do Zendesk",
                Confianca = 1.0,
                Origem = FieldSource.Extensao,
            });

        // O número do discador (identificador de chamadas) é o telefone de quem está na
        // linha AGORA. Ele já era coletado e ficava só no cabeçalho do registro; sem
        // estar entre os campos, a aba "Dados extraídos" mostrava "Não identificado"
        // para o telefone que o app conhecia desde o primeiro segundo da ligação.
        if (NormalizarTelefone(metadata.Numero) is { } discador)
            ObjectiveFields.Mesclar(campos.Telefones, new ExtractedValue
            {
                Tipo = FieldType.Telefone,
                Valor = discador,
                TrechoOrigem = "número da ligação (discador)",
                Confianca = 1.0,
                Origem = FieldSource.Extensao,
            });

        // Nome do solicitante. Não existe extração de nome a partir da fala — nem aqui nem
        // no servidor —, então esta é a única fonte: ou vem do cadastro, ou não vem.
        if (NormalizarNome(metadata.NomeCliente) is { } nome)
            ObjectiveFields.Mesclar(campos.Nomes, new ExtractedValue
            {
                Tipo = FieldType.Nome,
                Valor = nome,
                TrechoOrigem = "cadastro do Zendesk",
                Confianca = 1.0,
                Origem = FieldSource.Extensao,
            });

        // O ticket já vinha no cabeçalho do registro e ficava fora dos campos: o atendente
        // via "Protocolos: não identificado" numa ligação que tinha ticket desde o início.
        if (NormalizarTexto(metadata.TicketId) is { } ticket)
            ObjectiveFields.Mesclar(campos.Protocolos, new ExtractedValue
            {
                Tipo = FieldType.Protocolo,
                Valor = ticket,
                TrechoOrigem = "ticket do Zendesk",
                Confianca = 1.0,
                Origem = FieldSource.Extensao,
            });

        campos.Ordenar();
    }

    /// <summary>
    /// Nome plausível vindo do DOM: preserva a grafia do cadastro (é assim que o atendente
    /// vai copiar), mas recusa rótulo e lixo — precisa ter letra e ao menos dois caracteres.
    /// </summary>
    private static string? NormalizarNome(string? bruto)
    {
        var t = ColapsarEspacos(bruto);
        if (t is null || t.Length < 2) return null;
        return t.Any(char.IsLetter) ? t : null;
    }

    /// <summary>Texto do cadastro usado como valor (ticket): só limpa espaços.</summary>
    private static string? NormalizarTexto(string? bruto) => ColapsarEspacos(bruto);

    private static string? ColapsarEspacos(string? bruto)
    {
        if (string.IsNullOrWhiteSpace(bruto)) return null;
        var t = string.Join(' ', bruto.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return t.Length == 0 ? null : t;
    }

    /// <summary>Devolve o e-mail em minúsculas, ou null se não parecer um e-mail.</summary>
    private static string? NormalizarEmail(string? bruto)
    {
        var t = bruto?.Trim();
        if (string.IsNullOrEmpty(t)) return null;
        t = t.ToLowerInvariant();
        return ReEmail.IsMatch(t) ? t : null;
    }

    /// <summary>
    /// Reduz o telefone a dígitos no formato nacional. O Zendesk guarda em E.164
    /// ("+5511912345678") e o discador costuma exibir com máscara; ambos precisam virar
    /// a mesma coisa para deduplicar contra o que a regra achou na transcrição.
    /// </summary>
    private static string? NormalizarTelefone(string? bruto)
    {
        if (string.IsNullOrWhiteSpace(bruto)) return null;

        var digitos = new string(bruto.Where(char.IsDigit).ToArray());

        // Prefixo do Brasil só é descartado quando o que sobra é um número nacional
        // completo — "55" também é o DDD de Caxias do Sul.
        if (digitos.Length is 12 or 13 && digitos.StartsWith("55", StringComparison.Ordinal))
            digitos = digitos[2..];

        // 10 = fixo com DDD, 11 = celular com DDD. Fora disso (ramal, número curto de
        // serviço, lixo do DOM) não é telefone de cliente e não entra como se fosse.
        return digitos.Length is 10 or 11 ? digitos : null;
    }
}
