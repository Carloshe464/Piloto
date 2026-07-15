using Piloto.Core.Text;
using Xunit;

namespace Piloto.Tests;

public class PortugueseNumberParserTests
{
    [Theory]
    [InlineData("trezentos e vinte e cinco", "325")]
    [InlineData("noventa e nove", "99")]
    [InlineData("dois mil e quinhentos", "2500")]
    [InlineData("mil", "1000")]
    [InlineData("cento e vinte e três mil quatrocentos e cinquenta", "123450")]
    public void ConverteCardinais(string entrada, string esperado)
        => Assert.Equal(esperado, PortugueseNumberParser.Converter(entrada));

    [Theory]
    [InlineData("meia nove um dois", "6912")]
    [InlineData("quatro nove um dois", "4912")]
    [InlineData("zero um dois três", "0123")]
    public void ConverteSequenciasDeDigitos(string entrada, string esperado)
        => Assert.Equal(esperado, PortugueseNumberParser.Converter(entrada));

    [Fact]
    public void PreservaTextoNaoNumerico()
    {
        var r = PortugueseNumberParser.Converter("bom dia tudo bem");
        Assert.Equal("bom dia tudo bem", r);
    }

    [Fact]
    public void ConverteNumeroEmMeioDeFrase()
    {
        var r = PortugueseNumberParser.Converter("o valor é cinquenta reais");
        Assert.Contains("50", r);
        Assert.Contains("reais", r);
    }
}
