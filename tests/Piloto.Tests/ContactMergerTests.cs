using Piloto.Core.Models;
using Piloto.Core.Pipeline;
using Piloto.Core.Text;
using Piloto.Rules;
using Xunit;

namespace Piloto.Tests;

public class ContactMergerTests
{
    private readonly RuleExtractor _rules = new(new TextNormalizer());

    [Fact]
    public void EmailDoCadastroEntraComoOrigemExtensao()
    {
        var campos = ObjectiveFields.Vazio();
        ContactMerger.Aplicar(campos, new CallMetadata { EmailCliente = "Joao.Silva@Empresa.com" });

        var email = Assert.Single(campos.Emails);
        Assert.Equal("joao.silva@empresa.com", email.Valor);
        Assert.Equal(FieldSource.Extensao, email.Origem);
        Assert.Equal(1.0, email.Confianca);
    }

    [Fact]
    public void EmailDoCadastroSubstituiOMesmoEmailOuvidoNaLigacao()
    {
        // O Whisper acertou o e-mail; o cadastro confirma. Vira UMA linha, com a
        // procedência mais forte — não duas linhas iguais com confianças diferentes.
        var campos = _rules.Extrair(TestData.Fala("manda pro joao.silva@empresa.com obrigado"));
        Assert.Equal(FieldSource.Regra, Assert.Single(campos.Emails).Origem);

        ContactMerger.Aplicar(campos, new CallMetadata { EmailCliente = "joao.silva@empresa.com" });

        var email = Assert.Single(campos.Emails);
        Assert.Equal(FieldSource.Extensao, email.Origem);
    }

    [Fact]
    public void EmailDitadoDiferenteDoCadastroConviveComEle()
    {
        // "anota o meu outro e-mail" é caso real: os dois valem, o do cadastro primeiro.
        var campos = _rules.Extrair(TestData.Fala("anota o outro, contato@outrodominio.com.br"));
        ContactMerger.Aplicar(campos, new CallMetadata { EmailCliente = "joao.silva@empresa.com" });

        Assert.Equal(2, campos.Emails.Count);
        Assert.Equal(FieldSource.Extensao, campos.Emails[0].Origem);
        Assert.Equal("joao.silva@empresa.com", campos.Emails[0].Valor);
    }

    [Theory]
    [InlineData("+55 (11) 91234-5678", "11912345678")]
    [InlineData("5511912345678", "11912345678")]
    [InlineData("(11) 3456-7890", "1134567890")]
    [InlineData("11912345678", "11912345678")]
    public void TelefoneDoCadastroENormalizadoParaOFormatoNacional(string bruto, string esperado)
    {
        var campos = ObjectiveFields.Vazio();
        ContactMerger.Aplicar(campos, new CallMetadata { TelefoneCliente = bruto });

        Assert.Equal(esperado, Assert.Single(campos.Telefones).Valor);
    }

    [Theory]
    [InlineData("1234")]          // ramal
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("não informado")] // rótulo capturado por seletor errado
    public void TelefoneImplausivelDoDomNaoViraCampo(string bruto)
    {
        var campos = ObjectiveFields.Vazio();
        ContactMerger.Aplicar(campos, new CallMetadata { TelefoneCliente = bruto });

        Assert.Empty(campos.Telefones);
    }

    [Theory]
    [InlineData("E-mail")]              // rótulo do DOM, não valor
    [InlineData("sem-arroba.com")]
    [InlineData("a@b")]                 // sem domínio de topo
    public void EmailImplausivelDoDomNaoViraCampo(string bruto)
    {
        var campos = ObjectiveFields.Vazio();
        ContactMerger.Aplicar(campos, new CallMetadata { EmailCliente = bruto });

        Assert.Empty(campos.Emails);
    }

    [Fact]
    public void NumeroDoDiscadorViraTelefoneNosCamposObjetivos()
    {
        // Era o furo principal: o app conhecia o número desde o primeiro segundo da
        // ligação e a aba "Dados extraídos" mostrava "Não identificado".
        var campos = ObjectiveFields.Vazio();
        ContactMerger.Aplicar(campos, new CallMetadata { Numero = "+55 11 91234-5678" });

        var tel = Assert.Single(campos.Telefones);
        Assert.Equal("11912345678", tel.Valor);
        Assert.Equal(FieldSource.Extensao, tel.Origem);
    }

    [Fact]
    public void DiscadorETelefoneDoCadastroIguaisNaoDuplicam()
    {
        var campos = ObjectiveFields.Vazio();
        ContactMerger.Aplicar(campos, new CallMetadata
        {
            Numero = "(11) 91234-5678",
            TelefoneCliente = "+5511912345678",
        });

        Assert.Single(campos.Telefones);
    }

    [Fact]
    public void AplicarDuasVezesNaoDuplica()
    {
        // O reprocessamento a partir do áudio roda o pipeline de novo sobre o mesmo
        // metadado — não pode acumular linhas a cada passada.
        var campos = ObjectiveFields.Vazio();
        var metadata = new CallMetadata
        {
            EmailCliente = "joao@empresa.com",
            TelefoneCliente = "11912345678",
        };

        ContactMerger.Aplicar(campos, metadata);
        ContactMerger.Aplicar(campos, metadata);

        Assert.Single(campos.Emails);
        Assert.Single(campos.Telefones);
    }

    [Fact]
    public void MetadataVaziaNaoMexeNosCampos()
    {
        var campos = _rules.Extrair(TestData.Fala("meu número é (11) 91234-5678 tá"));
        ContactMerger.Aplicar(campos, CallMetadata.Vazio());

        var tel = Assert.Single(campos.Telefones);
        Assert.Equal(FieldSource.Regra, tel.Origem);
    }

    [Fact]
    public void ValorDoCadastroFicaAntesDoOuvidoNaMesmaCategoria()
    {
        var campos = _rules.Extrair(TestData.Fala("pode anotar 21 98888-7777 também"));
        ContactMerger.Aplicar(campos, new CallMetadata { TelefoneCliente = "11912345678" });

        Assert.Equal(2, campos.Telefones.Count);
        Assert.Equal(FieldSource.Extensao, campos.Telefones[0].Origem);
    }
}
