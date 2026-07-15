using Piloto.Core.Models;

namespace Piloto.Tests;

internal static class TestData
{
    public static Transcript Dialogo(params (Speaker Speaker, string Texto)[] falas)
    {
        var segmentos = falas.Select((f, i) => new TranscriptSegment
        {
            Speaker = f.Speaker,
            Inicio = TimeSpan.FromSeconds(i * 2),
            Fim = TimeSpan.FromSeconds(i * 2 + 1),
            Texto = f.Texto,
        });
        return new Transcript(segmentos);
    }

    public static Transcript Fala(string texto) => Dialogo((Speaker.Cliente, texto));
}
