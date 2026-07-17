using Piloto.Core.Configuration;
using Piloto.Llm;
using Xunit;

namespace Piloto.Tests;

public class GbnfGrammarBuilderTests
{
    private static ListasFechadas Listas() => new()
    {
        MotivoContato = new List<string> { "Dúvida", "Reclamação", "Segunda via" },
        Produto = new List<string> { "Site", "Plano Premium" },
        Status = new List<string> { "Resolvido", "Pendente" },
    };

    [Fact]
    public void TodaLinhaNaoVaziaDefineUmaRegraCompleta()
    {
        // No GBNF do llama.cpp, quebra de linha fora de parênteses encerra a regra:
        // uma regra em múltiplas linhas é gramática malformada e DERRUBA O PROCESSO
        // nativamente na criação do sampler. Cada linha não vazia precisa conter "::=".
        var gramatica = GbnfGrammarBuilder.Construir(Listas());

        foreach (var linha in gramatica.Split('\n'))
        {
            var l = linha.TrimEnd('\r').Trim();
            if (l.Length == 0) continue;
            Assert.Contains("::=", l);
        }
    }

    [Fact]
    public void RootContemTodasAsChavesDoJson()
    {
        var gramatica = GbnfGrammarBuilder.Construir(Listas());
        var root = gramatica.Split('\n').First(l => l.StartsWith("root"));

        foreach (var chave in new[] { "resumo", "motivo_contato", "produto", "status", "pedido", "proximo_passo" })
            Assert.Contains(chave, root);
    }

    [Fact]
    public void ValoresDasListasEntramComoLiteraisComNull()
    {
        var gramatica = GbnfGrammarBuilder.Construir(Listas());

        Assert.Contains("Dúvida", gramatica);
        Assert.Contains("Reclamação", gramatica);
        Assert.Contains("Plano Premium", gramatica);
        Assert.Contains("\"null\"", gramatica);
    }
}
