using System.Text.Json;
using System.Text.Json.Serialization;

namespace Piloto.Core.Configuration;

/// <summary>
/// Aplica o endereço e o token que o instalador perguntou ao operador.
/// <para>
/// O instalador não escreve a configuração do usuário diretamente: ele roda elevado, e
/// <c>%LOCALAPPDATA%</c> sob elevação aponta para o perfil do administrador, não para o do
/// atendente. Ele grava <c>servidor.json</c> na pasta do programa e o app aplica na
/// primeira abertura, já como o usuário certo — ninguém precisa editar JSON com permissão
/// de administrador.
/// </para>
/// <para>
/// A data do arquivo decide. Aplicar sempre sobrescreveria o que o atendente ajustou na
/// tela; nunca aplicar ignoraria uma reinstalação feita de propósito para trocar de
/// servidor.
/// </para>
/// </summary>
public static class ProvisionamentoServidor
{
    private static readonly JsonSerializerOptions Opcoes = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// Aplica o arquivo a <paramref name="settings"/>. Devolve <c>true</c> quando algo
    /// mudou — e só então vale a pena gravar.
    /// </summary>
    public static bool Aplicar(AppSettings settings, string caminhoArquivo)
    {
        if (!File.Exists(caminhoArquivo))
            return false;

        DateTimeOffset carimbo;
        Dados? dados;
        try
        {
            carimbo = new DateTimeOffset(File.GetLastWriteTimeUtc(caminhoArquivo), TimeSpan.Zero);
            dados = JsonSerializer.Deserialize<Dados>(File.ReadAllText(caminhoArquivo), Opcoes);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            // Provisionamento é conveniência: arquivo corrompido não pode impedir o app de
            // abrir. Ele sobe com a configuração que já tinha e o atendente corrige na tela.
            return false;
        }

        if (dados is null)
            return false;

        // Já aplicado: não reescrever por cima do que o atendente ajustou depois.
        if (settings.Servidor.ProvisionadoEm is { } aplicado && aplicado >= carimbo)
            return false;

        if (!string.IsNullOrWhiteSpace(dados.Url))
            settings.Servidor.Url = dados.Url.Trim();
        if (!string.IsNullOrWhiteSpace(dados.Token))
            settings.Servidor.Token = dados.Token.Trim();

        // Carimba mesmo quando os valores vieram iguais: sem isso o arquivo seria
        // reavaliado a cada abertura, e uma edição feita na tela seria desfeita na
        // seguinte.
        settings.Servidor.ProvisionadoEm = carimbo;
        return true;
    }

    private sealed record Dados
    {
        [JsonPropertyName("url")] public string? Url { get; init; }
        [JsonPropertyName("token")] public string? Token { get; init; }
    }
}
