namespace Piloto.Core.Models;

/// <summary>
/// Tipo de campo objetivo exibido. São os cinco dados que o atendente copia para o
/// cadastro: ticket e telefone vêm da extensão; CPF/CNPJ, nome e e-mail vêm da
/// transcrição do servidor (ou do cadastro, quando ele venceu a transcrição lá).
/// <para>Data, Valor e Protocolo saíram: o servidor não extrai nenhum dos três, então
/// só ocupavam a tela com "Não identificado".</para>
/// </summary>
public enum FieldType
{
    Ticket,
    Telefone,
    Cpf,
    Cnpj,
    Nome,
    Email,
}

/// <summary>De onde veio o valor. Determina a confiança e o que o atendente pode copiar
/// sem conferir: o Zendesk é cadastro, a transcrição é o que o Whisper entendeu.</summary>
public enum FieldSource
{
    /// <summary>Camada 1 — regex sobre a transcrição. Sujeito a erro de reconhecimento.</summary>
    Regra,

    /// <summary>Lido do DOM do Zendesk pela extensão. É dado cadastrado, não ouvido.</summary>
    Extensao,
}

/// <summary>
/// Um valor detectado pela camada de regras, com o texto normalizado, a confiança
/// (0..1) e o trecho de origem para rastreabilidade / grounding.
/// </summary>
public sealed class ExtractedValue
{
    public required FieldType Tipo { get; init; }

    /// <summary>Valor normalizado/canônico (ex.: CPF só com dígitos, valor em centavos formatado).</summary>
    public required string Valor { get; init; }

    /// <summary>Como apareceu no texto (para exibição e auditoria).</summary>
    public required string TrechoOrigem { get; init; }

    /// <summary>0..1 — quanto a regra confia nesta detecção.</summary>
    public required double Confianca { get; init; }

    /// <summary>Não é <c>required</c> e tem default: o JSON já persistido (sem o campo)
    /// continua desserializando como <see cref="FieldSource.Regra"/>, que é o que ele era.</summary>
    public FieldSource Origem { get; init; } = FieldSource.Regra;

    /// <summary>Valor vindo do cadastro do Zendesk — não precisa de conferência no áudio.</summary>
    public bool EhDoCadastro => Origem == FieldSource.Extensao;

    public override string ToString() => $"{Tipo}={Valor} ({Confianca:P0})";
}

/// <summary>
/// Resultado da Camada 1 (REGRAS): listas de valores objetivos detectados na transcrição.
/// </summary>
public sealed class ObjectiveFields
{
    /// <summary>Número do ticket, lido do Zendesk pela extensão.</summary>
    public List<ExtractedValue> Tickets { get; init; } = new();

    public List<ExtractedValue> Telefones { get; init; } = new();

    /// <summary>Documentos (CPF e CNPJ) — mantém o nome "Cpfs" pela compatibilidade do
    /// JSON persistido; o <see cref="ExtractedValue.Tipo"/> distingue os dois.</summary>
    public List<ExtractedValue> Cpfs { get; init; } = new();

    /// <summary>Nome do solicitante, ouvido na ligação ou vindo do cadastro.</summary>
    public List<ExtractedValue> Nomes { get; init; } = new();

    public List<ExtractedValue> Emails { get; init; } = new();

    public IEnumerable<ExtractedValue> Todos()
        => Tickets.Concat(Telefones).Concat(Cpfs).Concat(Nomes).Concat(Emails);

    /// <summary>Todas as listas, na ordem em que aparecem na tela e nas exportações.</summary>
    public IEnumerable<(string Titulo, List<ExtractedValue> Valores)> PorCategoria()
    {
        yield return ("Ticket", Tickets);
        yield return ("Telefones", Telefones);
        yield return ("CPF/CNPJ", Cpfs);
        yield return ("Nome", Nomes);
        yield return ("E-mails", Emails);
    }

    /// <summary>
    /// Insere respeitando a identidade do valor: a mesma informação dita cinco vezes na
    /// ligação é UM campo, não cinco. Quando o valor já existe, sobrevive a detecção mais
    /// forte (cadastro do Zendesk &gt; regra; entre regras, a de maior confiança) — assim o
    /// e-mail lido do Zendesk substitui o mesmo e-mail que o Whisper ouviu pela metade.
    /// </summary>
    public static void Mesclar(List<ExtractedValue> destino, ExtractedValue novo)
    {
        var chave = Chave(novo);
        for (var i = 0; i < destino.Count; i++)
        {
            if (Chave(destino[i]) != chave) continue;
            if (Ranque(novo) > Ranque(destino[i])) destino[i] = novo;
            return;
        }
        destino.Add(novo);
    }

    /// <summary>Ordena cada lista pela força da detecção: o que o atendente pode copiar
    /// sem conferir vem primeiro; o palpite de baixa confiança afunda.</summary>
    public void Ordenar()
    {
        foreach (var (_, valores) in PorCategoria())
        {
            var ordenado = valores.OrderByDescending(Ranque).ToList();
            valores.Clear();
            valores.AddRange(ordenado);
        }
    }

    private static double Ranque(ExtractedValue v)
        => (v.Origem == FieldSource.Extensao ? 10 : 0) + v.Confianca;

    /// <summary>Identidade do valor para deduplicação: compara o conteúdo, não a grafia.
    /// "(11) 91234-5678" e "11912345678" são o mesmo telefone; e-mail ignora caixa.</summary>
    private static string Chave(ExtractedValue v) => v.Tipo switch
    {
        // Ticket fica de fora: o identificador do Zendesk nem sempre é só dígito.
        FieldType.Telefone or FieldType.Cpf or FieldType.Cnpj
            => new string(v.Valor.Where(char.IsDigit).ToArray()),
        FieldType.Email or FieldType.Nome => v.Valor.Trim().ToLowerInvariant(),
        _ => v.Valor.Trim(),
    };

    public static ObjectiveFields Vazio() => new();
}
