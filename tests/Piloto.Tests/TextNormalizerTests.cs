using Piloto.Core.Text;
using Xunit;

namespace Piloto.Tests;

public class TextNormalizerTests
{
    private readonly TextNormalizer _n = new();

    [Fact]
    public void NormalizaEmailDitado()
    {
        var r = _n.Normalizar("meu email é joao arroba empresa ponto com");
        Assert.Contains("joao@empresa.com", r);
    }

    [Fact]
    public void NormalizaNumerosFalados()
    {
        var r = _n.Normalizar("são trezentos e vinte e cinco reais");
        Assert.Contains("325", r);
    }

    [Fact]
    public void ColapsaEspacos()
    {
        var r = _n.Normalizar("texto    com     espacos");
        Assert.Equal("texto com espacos", r);
    }

    [Fact]
    public void TextoVazioNaoQuebra()
    {
        Assert.Equal(string.Empty, _n.Normalizar(""));
    }
}
