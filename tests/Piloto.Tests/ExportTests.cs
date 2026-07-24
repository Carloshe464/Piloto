using System.Text.Json;
using Piloto.Core.Abstractions;
using Piloto.Core.Models;
using Piloto.Data.Export;
using Xunit;

namespace Piloto.Tests;

public class ExportTests
{
    private static CallRecord Registro() => new()
    {
        Id = 7,
        Metadata = new CallMetadata { Numero = "11912345678", TicketId = "T-42" },
        Transcript = TestData.Dialogo(
            (Speaker.Atendente, "bom dia"),
            (Speaker.Cliente, "quero a segunda via do boleto")),
        Resumo = new LlmSummary { MotivoContato = "Segunda via de boleto", Status = "Resolvido", Resumo = "Cliente pediu boleto." },
        Duracao = TimeSpan.FromSeconds(42),
    };

    private readonly RecordExporter _exporter = new();

    [Fact]
    public void TxtContemSecoesEsperadas()
    {
        var txt = _exporter.Exportar(Registro(), ExportFormat.Txt);
        Assert.Contains("REGISTRO DE LIGAÇÃO", txt);
        Assert.Contains("RESUMO", txt);
        Assert.Contains("DIÁLOGO", txt);
    }

    [Fact]
    public void JsonEhValido()
    {
        var json = _exporter.Exportar(Registro(), ExportFormat.Json);
        using var doc = JsonDocument.Parse(json); // não lança se for válido
        Assert.Equal(7, doc.RootElement.GetProperty("Id").GetInt32());
    }

    [Fact]
    public void CsvTemCabecalhoEUmaLinha()
    {
        var csv = _exporter.Exportar(Registro(), ExportFormat.Csv);
        var linhas = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, linhas.Length);
        Assert.StartsWith("id;criado_em", linhas[0]);
    }

    [Fact]
    public void CpfECnpjSaemSemMascaraNosCamposObjetivos()
    {
        var registro = Registro();
        registro.Campos.Cpfs.Add(new ExtractedValue
        {
            Tipo = FieldType.Cpf, Valor = "111.444.777-35", TrechoOrigem = "111.444.777-35", Confianca = 0.95,
        });
        registro.Campos.Cpfs.Add(new ExtractedValue
        {
            Tipo = FieldType.Cnpj, Valor = "12.344.567/0001-11", TrechoOrigem = "12344567 0001 11", Confianca = 0.6,
        });

        var txt = _exporter.Exportar(registro, ExportFormat.Txt); // máscara de PII ligada (padrão)
        Assert.Contains("CPF/CNPJ: 111.444.777-35", txt);
        Assert.Contains("12.344.567/0001-11", txt);
    }

    [Fact]
    public void TxtDistingueOValorDoCadastroDoValorOuvido()
    {
        var registro = Registro();
        registro.Metadata.NomeCliente = "Maria Souza";
        registro.Campos.Emails.Add(new ExtractedValue
        {
            Tipo = FieldType.Email, Valor = "maria@empresa.com", TrechoOrigem = "cadastro do Zendesk",
            Confianca = 1.0, Origem = FieldSource.Extensao,
        });
        registro.Campos.Protocolos.Add(new ExtractedValue
        {
            Tipo = FieldType.Protocolo, Valor = "20250715123", TrechoOrigem = "protocolo 20250715123", Confianca = 0.5,
        });

        var txt = _exporter.Exportar(registro, ExportFormat.Txt, mascararPii: false);
        Assert.Contains("Cliente (cadastro Zendesk): Maria Souza", txt);
        Assert.Contains("maria@empresa.com (cadastro Zendesk)", txt);
        Assert.Contains("20250715123 (50%)", txt);
    }

    [Fact]
    public void ExportacaoMascaraContatoVindoDoCadastro()
    {
        // O valor ser confiável não o torna menos PII: o que SAI do app continua
        // mascarado quando a opção está ligada.
        var registro = Registro();
        registro.Campos.Emails.Add(new ExtractedValue
        {
            Tipo = FieldType.Email, Valor = "maria@empresa.com", TrechoOrigem = "cadastro do Zendesk",
            Confianca = 1.0, Origem = FieldSource.Extensao,
        });

        var txt = _exporter.Exportar(registro, ExportFormat.Txt);
        Assert.DoesNotContain("maria@empresa.com", txt);
        Assert.Contains("m***@empresa.com", txt);
    }

    [Theory]
    [InlineData("111.444.777-35", "***.***.***-35")]
    public void MascaraCpf(string entrada, string esperado)
        => Assert.Equal(esperado, PiiMasker.Mascarar(entrada));

    [Fact]
    public void MascaraTelefoneEEmail()
    {
        Assert.Equal("*******5678", PiiMasker.Mascarar("11912345678"));
        Assert.Equal("j***@x.com", PiiMasker.Mascarar("joao@x.com"));
    }
}
