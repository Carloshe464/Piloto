using System.Text.Json;
using System.Text.Json.Serialization;
using Piloto.Core.Models;

namespace Piloto.Data;

/// <summary>(De)serialização dos objetos ricos do registro para colunas JSON do SQLite.</summary>
internal static class CallSerialization
{
    public static readonly JsonSerializerOptions Opts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private sealed record SegmentDto(string Speaker, double InicioSeg, double FimSeg, string Texto);

    public static string SerializarTranscript(Transcript t)
    {
        var dtos = t.Segmentos.Select(s => new SegmentDto(
            s.Speaker.ToString(), s.Inicio.TotalSeconds, s.Fim.TotalSeconds, s.Texto));
        return JsonSerializer.Serialize(dtos, Opts);
    }

    public static Transcript DeserializarTranscript(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Transcript.Vazio();
        var dtos = JsonSerializer.Deserialize<List<SegmentDto>>(json, Opts) ?? new();
        var segs = dtos.Select(d => new TranscriptSegment
        {
            Speaker = Enum.TryParse<Speaker>(d.Speaker, out var sp) ? sp : Speaker.Cliente,
            Inicio = TimeSpan.FromSeconds(d.InicioSeg),
            Fim = TimeSpan.FromSeconds(d.FimSeg),
            Texto = d.Texto,
        });
        return new Transcript(segs);
    }

    public static string Serializar<T>(T obj) => JsonSerializer.Serialize(obj, Opts);

    public static T Deserializar<T>(string? json, T fallback)
        => string.IsNullOrWhiteSpace(json) ? fallback : (JsonSerializer.Deserialize<T>(json!, Opts) ?? fallback);
}
