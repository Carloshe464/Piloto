using Piloto.Core.Models;
using Piloto.Core.Text;
using Piloto.Rules;
using Xunit;

namespace Piloto.Tests;

public class RuleExtractorTests
{
    private readonly RuleExtractor _rules = new(new TextNormalizer());

    [Fact]
    public void ExtraiCpfValidoComAltaConfianca()
    {
        var campos = _rules.Extrair(TestData.Fala("meu cpf é 111.444.777-35 por favor"));
        var cpf = Assert.Single(campos.Cpfs);
        Assert.Equal("111.444.777-35", cpf.Valor);
        Assert.True(cpf.Confianca >= 0.9);
    }

    [Fact]
    public void CpfComDigitoInvalidoTemConfiancaBaixa()
    {
        var campos = _rules.Extrair(TestData.Fala("o número é 123.456.789-00 anotado"));
        var cpf = Assert.Single(campos.Cpfs);
        Assert.True(cpf.Confianca < 0.9);
    }

    [Fact]
    public void ExtraiCnpjFormatadoValido()
    {
        // 11.222.333/0001-81 tem verificadores válidos.
        var campos = _rules.Extrair(TestData.Fala("o cnpj é 11.222.333/0001-81 da empresa"));
        var cnpj = Assert.Single(campos.Cpfs);
        Assert.Equal(FieldType.Cnpj, cnpj.Tipo);
        Assert.Equal("11.222.333/0001-81", cnpj.Valor);
        Assert.True(cnpj.Confianca >= 0.9);
    }

    [Fact]
    public void ExtraiCnpjDitadoComMilContra()
    {
        // Como o Whisper transcreveu em campo: dígitos soltos + "1000 contra" no lugar de "/0001-".
        var campos = _rules.Extrair(TestData.Fala("claro, é 1, 2, 3, 4, 4, 5, 6, 7, 1000 contra 11."));
        var cnpj = Assert.Single(campos.Cpfs);
        Assert.Equal(FieldType.Cnpj, cnpj.Tipo);
        Assert.Equal("12.344.567/0001-11", cnpj.Valor);
        Assert.Empty(campos.Telefones);
        Assert.Empty(campos.Protocolos);
    }

    [Fact]
    public void QuatorzeDigitosSemFormatoDeCnpjNaoViraCnpj()
    {
        // Sem "/" nem filial 0001 e com verificadores inválidos: fica para protocolo.
        var campos = _rules.Extrair(TestData.Fala("protocolo 12345678999912 anotado"));
        Assert.Empty(campos.Cpfs);
    }

    [Fact]
    public void ExtraiTelefoneFormatado()
    {
        var campos = _rules.Extrair(TestData.Fala("meu número é (11) 91234-5678 tá"));
        var tel = Assert.Single(campos.Telefones);
        Assert.Equal("11912345678", tel.Valor);
    }

    [Fact]
    public void ExtraiEmail()
    {
        var campos = _rules.Extrair(TestData.Fala("manda pro joao.silva@empresa.com obrigado"));
        var email = Assert.Single(campos.Emails);
        Assert.Equal("joao.silva@empresa.com", email.Valor);
    }

    [Fact]
    public void ExtraiValorEmReais()
    {
        var campos = _rules.Extrair(TestData.Fala("ficou R$ 1.234,56 no total"));
        Assert.Contains(campos.Valores, v => v.Valor.Contains("1.234,56"));
    }

    [Fact]
    public void ExtraiDataNumerica()
    {
        var campos = _rules.Extrair(TestData.Fala("o vencimento é 15/07/2026 certo"));
        Assert.Contains(campos.Datas, d => d.Valor == "15/07/2026");
    }

    [Fact]
    public void ProtocoloComRotuloVenceTelefone()
    {
        var campos = _rules.Extrair(TestData.Fala("seu protocolo 20250715123 foi aberto"));
        Assert.Contains(campos.Protocolos, p => p.Valor == "20250715123");
        Assert.Empty(campos.Telefones);
    }

    [Fact]
    public void TranscricaoVaziaNaoQuebra()
    {
        var campos = _rules.Extrair(Transcript.Vazio());
        Assert.Empty(campos.Todos());
    }
}
