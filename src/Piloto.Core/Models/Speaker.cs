namespace Piloto.Core.Models;

/// <summary>
/// Quem fala em um trecho. A separação vem dos 2 canais físicos capturados
/// (microfone = atendente, loopback do navegador = cliente) — não há diarização por IA.
/// </summary>
public enum Speaker
{
    Atendente,
    Cliente,
}

public static class SpeakerExtensions
{
    public static string Rotulo(this Speaker s) => s switch
    {
        Speaker.Atendente => "Atendente",
        Speaker.Cliente => "Cliente",
        _ => s.ToString(),
    };
}
