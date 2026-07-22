using Piloto.Core.Models;
using Xunit;

namespace Piloto.Tests;

public class TranscriptSanitizerTests
{
    private static TranscriptSegment Seg(double iniSeg, double fimSeg, string texto = "fala") => new()
    {
        Speaker = Speaker.Cliente,
        Inicio = TimeSpan.FromSeconds(iniSeg),
        Fim = TimeSpan.FromSeconds(fimSeg),
        Texto = texto,
    };

    [Fact]
    public void ComprimeTimestampsAlemDoAudio()
    {
        // Assinatura dos registros 29/34: fala "aos 130 s" de um áudio de 80 s.
        var segs = new List<TranscriptSegment> { Seg(0, 30), Seg(60, 130) };

        var fator = TranscriptSanitizer.ComprimirTimestamps(segs, TimeSpan.FromSeconds(80));

        Assert.NotNull(fator);
        Assert.True(segs[^1].Fim <= TimeSpan.FromSeconds(80.5));
        Assert.True(segs[0].Fim < TimeSpan.FromSeconds(30)); // tudo escala junto, ordem preservada
    }

    [Fact]
    public void NaoMexeEmTimestampsDentroDoAudio()
    {
        var segs = new List<TranscriptSegment> { Seg(0, 30), Seg(40, 79) };
        var fator = TranscriptSanitizer.ComprimirTimestamps(segs, TimeSpan.FromSeconds(80));
        Assert.Null(fator);
        Assert.Equal(TimeSpan.FromSeconds(79), segs[^1].Fim);
    }

    [Fact]
    public void EstouroPequenoDaUltimaJanelaEhTolerado()
    {
        var segs = new List<TranscriptSegment> { Seg(0, 81) };
        Assert.Null(TranscriptSanitizer.ComprimirTimestamps(segs, TimeSpan.FromSeconds(80)));
    }

    [Fact]
    public void ColapsaLoopDeRepeticao()
    {
        // "Obrigado." em série com confiança alta: alucinação sobre música/ruído.
        var segs = new List<TranscriptSegment>
        {
            Seg(0, 2, "Bom dia."),
            Seg(3, 4, "Obrigado."),
            Seg(5, 6, "obrigado"),
            Seg(7, 8, "Obrigado."),
            Seg(9, 10, "Obrigado."),
            Seg(11, 12, "Tchau."),
        };

        var removidos = TranscriptSanitizer.ColapsarRepeticoes(segs);

        Assert.Equal(3, removidos);
        Assert.Equal(3, segs.Count); // Bom dia + 1 Obrigado + Tchau
        Assert.Equal("Obrigado.", segs[1].Texto);
    }

    [Fact]
    public void RepeticaoDuplaEhFalaRealENaoColapsa()
    {
        var segs = new List<TranscriptSegment> { Seg(0, 1, "Alô?"), Seg(2, 3, "Alô?") };
        Assert.Equal(0, TranscriptSanitizer.ColapsarRepeticoes(segs));
        Assert.Equal(2, segs.Count);
    }
}
