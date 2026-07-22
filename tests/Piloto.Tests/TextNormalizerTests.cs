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

    [Fact]
    public void ColapsaDigitosDitadosComVirgula()
    {
        var r = _n.Normalizar("é 1, 2, 3, 4, 4, 5, 6, 7, certo?");
        Assert.Contains("12344567", r);
    }

    [Fact]
    public void ColapsaDigitosDitadosComEspaco()
    {
        var r = _n.Normalizar("anota aí 9 9 8 7 6 5 4 3 2 1");
        Assert.Contains("9987654321", r);
    }

    [Fact]
    public void NaoColapsaListasCurtasDeNumeros()
    {
        var r = _n.Normalizar("nos dias 2, 3 e 4 de julho");
        Assert.DoesNotContain("234", r);
    }

    [Fact]
    public void MilContraViraFilialDeCnpj()
    {
        // Whisper transcreve "barra zero zero zero um traço" como "1000 contra" ou "mil contra".
        var r = _n.Normalizar("é 1, 2, 3, 4, 4, 5, 6, 7, 1000 contra 11.");
        Assert.Contains("12344567", r);
        Assert.Contains("0001-11", r);
    }

    [Fact]
    public void MilContraPorExtensoTambemConverte()
    {
        var r = _n.Normalizar("o CNPJ é 12345678 mil contra 90");
        Assert.Contains("0001-90", r);
    }

    [Fact]
    public void MilComPontoDeMilharTambemViraFilial()
    {
        // O Whisper small formata "mil" como "1.000" — variante real do registro 34.
        var r = _n.Normalizar("é 1, 2, 3, 4, 4, 5, 6, 7, 1.000 contra 11.");
        Assert.Contains("12344567", r);
        Assert.Contains("0001-11", r);
    }

    [Fact]
    public void ColapsoNaoEngoleDigitoDeNumeroComposto()
    {
        // O "1" de "1.000" não pode ser tragado pelo run de dígitos soltos.
        var r = _n.Normalizar("são 2, 4, 6, 8 e depois 1.000 unidades");
        Assert.Contains("2468", r);
        Assert.Contains("1.000", r);
    }

    [Fact]
    public void MilContraEmProsaComumFicaIntacto()
    {
        var r = _n.Normalizar("eram mil contra um naquela disputa");
        Assert.DoesNotContain("0001", r);
    }

    [Fact]
    public void BarraETracoDitadosViramSeparadores()
    {
        var r = _n.Normalizar("12345678 barra 0001 traço 90");
        Assert.Contains("12345678/0001-90", r);
    }
}
