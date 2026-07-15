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
