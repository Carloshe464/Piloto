using Piloto.Core.Configuration;
using Piloto.Core.Grounding;
using Piloto.Core.Models;
using Piloto.Core.Text;
using Xunit;

namespace Piloto.Tests;

public class GroundingCheckerTests
{
    private static ListasFechadas Listas() => new()
    {
        MotivoContato = new() { "Dúvida", "Reclamação" },
        Produto = new() { "Cartão", "Não se aplica" },
        Status = new() { "Resolvido", "Pendente de retorno" },
    };

    private readonly GroundingChecker _checker = new(new TextNormalizer());

    [Fact]
    public void ValorForaDaListaViraNullEMarcaRevisao()
    {
        var reg = new CallRecord
        {
            Transcript = TestData.Fala("cliente com dúvida sobre o cartão"),
            Resumo = new LlmSummary { MotivoContato = "Dúvida", Produto = "Geladeira", Status = "Resolvido" },
        };

        _checker.Aplicar(reg, Listas());

        Assert.Null(reg.Resumo.Produto);
        Assert.Equal("Dúvida", reg.Resumo.MotivoContato);
        Assert.True(reg.PrecisaRevisao);
    }

    [Fact]
    public void NumeroInexistenteNaTranscricaoMarcaRevisao()
    {
        var reg = new CallRecord
        {
            Transcript = TestData.Fala("seu protocolo é 12345"),
            Resumo = new LlmSummary
            {
                MotivoContato = "Dúvida",
                Produto = "Cartão",
                Status = "Resolvido",
                Pedido = "retornar no telefone 987654",
            },
        };

        _checker.Aplicar(reg, Listas());

        Assert.True(reg.PrecisaRevisao);
        Assert.Contains(reg.MotivosRevisao, m => m.Contains("987654"));
    }

    [Fact]
    public void RegistroConsistenteNaoMarcaRevisao()
    {
        var reg = new CallRecord
        {
            Transcript = TestData.Fala("cliente com dúvida, protocolo 12345 resolvido"),
            Resumo = new LlmSummary
            {
                MotivoContato = "Dúvida",
                Produto = "Cartão",
                Status = "Resolvido",
                Pedido = "verificar protocolo 12345",
            },
        };

        _checker.Aplicar(reg, Listas());

        Assert.False(reg.PrecisaRevisao);
    }
}
