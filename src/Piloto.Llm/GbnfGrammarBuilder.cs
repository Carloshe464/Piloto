using System.Text;
using Piloto.Core.Configuration;

namespace Piloto.Llm;

/// <summary>
/// Gera uma gramática GBNF que força a saída do LLM a um JSON com as chaves fixas e,
/// para <c>motivo_contato</c>, <c>produto</c> e <c>status</c>, restringe o valor às listas
/// fechadas (ou <c>null</c>). Assim o modelo <b>escolhe</b> — não redige.
/// </summary>
public static class GbnfGrammarBuilder
{
    public static string Construir(ListasFechadas listas)
    {
        var sb = new StringBuilder();

        // ATENÇÃO: no GBNF do llama.cpp, quebra de linha fora de parênteses ENCERRA a
        // regra — uma regra em múltiplas linhas vira gramática malformada e o parser
        // nativo derruba o processo inteiro na criação do sampler (sem exceção .NET).
        // Por isso a root é montada em UMA linha. Teste de regressão cobre isso.
        sb.AppendLine(
            "root ::= \"{\" ws "
            + Chave("resumo") + " ws string ws \",\" ws "
            + Chave("motivo_contato") + " ws motivo ws \",\" ws "
            + Chave("produto") + " ws produto ws \",\" ws "
            + Chave("status") + " ws status ws \",\" ws "
            + Chave("pedido") + " ws stringnull ws \",\" ws "
            + Chave("proximo_passo") + " ws stringnull ws "
            + "\"}\" ws");
        sb.AppendLine();
        sb.AppendLine("motivo  ::= " + Alternativas(listas.MotivoContato));
        sb.AppendLine("produto ::= " + Alternativas(listas.Produto));
        sb.AppendLine("status  ::= " + Alternativas(listas.Status));
        sb.AppendLine();
        sb.AppendLine("""
            stringnull ::= string | "null"
            string ::= "\"" char* "\""
            char   ::= [^"\\] | "\\" ["\\/bfnrt]
            ws     ::= [ \t\n]*
            """);

        return sb.ToString();
    }

    /// <summary>Literal GBNF de uma chave JSON: <c>"\"nome\":"</c>.</summary>
    private static string Chave(string nome) => "\"\\\"" + nome + "\\\":\"";

    /// <summary>Alternância de literais JSON entre os valores da lista e <c>null</c>.</summary>
    private static string Alternativas(IReadOnlyList<string> valores)
    {
        var literais = valores
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => "\"\\\"" + EscaparGbnf(v) + "\\\"\"");
        var todas = literais.Append("\"null\"");
        return string.Join(" | ", todas);
    }

    /// <summary>
    /// Escapa um valor para aparecer dentro de um literal de string GBNF.
    /// <para>
    /// Todo caractere fora do ASCII imprimível vira escape <c>\uXXXX</c> (suportado pelo
    /// GBNF do llama.cpp). Isso é OBRIGATÓRIO: o P/Invoke de AddGrammar no LLamaSharp
    /// 0.25 marshala a string em ANSI, então um "ú" cru chega como byte inválido em
    /// UTF-8 — o parser nativo rejeita a gramática, devolve NULL e o sampler nulo
    /// derruba o processo inteiro no primeiro token. ASCII puro sobrevive a qualquer
    /// marshaling. Teste de regressão cobre isso.
    /// </para>
    /// </summary>
    private static string EscaparGbnf(string valor)
    {
        var sb = new StringBuilder(valor.Length);
        foreach (var c in valor)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                default:
                    if (c < 0x20 || c > 0x7E)
                        sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else
                        sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }
}
