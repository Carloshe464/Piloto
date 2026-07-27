using System.Text.Json;
using System.Text.Json.Nodes;
using Piloto.Core.Configuration;

namespace Piloto.App.Services;

/// <summary>
/// Resolve a configuração editável do usuário. Na primeira execução, semeia
/// <c>%LOCALAPPDATA%\Piloto\config</c> a partir dos arquivos empacotados com o app,
/// e depois passa a ler/gravar dessa pasta (sobrevive a reinstalações).
/// </summary>
public sealed class ConfigService
{
    public string ConfigDir { get; }
    public string CaminhoAppSettings => Path.Combine(ConfigDir, "appsettings.json");
    public string CaminhoListas => Path.Combine(ConfigDir, "listas.json");
    public string CaminhoGlossario => Path.Combine(ConfigDir, "glossario.txt");

    public AppSettings Settings { get; private set; }

    private ConfigService(string configDir, AppSettings settings)
    {
        ConfigDir = configDir;
        Settings = settings;
    }

    public static ConfigService Inicializar()
    {
        var bundledDir = Path.Combine(AppContext.BaseDirectory, "config");
        var bootstrap = AppSettings.Load(Path.Combine(bundledDir, "appsettings.json"));

        var userDir = Path.Combine(bootstrap.PastaDadosExpandida, "config");
        Directory.CreateDirectory(userDir);

        foreach (var arquivo in new[] { "appsettings.json", "listas.json", "glossario.txt" })
        {
            var destino = Path.Combine(userDir, arquivo);
            var origem = Path.Combine(bundledDir, arquivo);
            if (!File.Exists(destino) && File.Exists(origem))
                File.Copy(origem, destino);
        }

        // Máquina já instalada TEM o appsettings.json dela, então a cópia acima não roda —
        // e uma seção nova (a do servidor de transcrição, por exemplo) nunca chegaria lá.
        // O app subiria mudo, apontando para o padrão compilado. Completar as chaves
        // ausentes a partir do arquivo empacotado é o que faz a atualização valer em campo.
        CompletarChavesNovas(
            Path.Combine(userDir, "appsettings.json"),
            Path.Combine(bundledDir, "appsettings.json"));

        var settings = AppSettings.Load(Path.Combine(userDir, "appsettings.json"));
        return new ConfigService(userDir, settings);
    }

    /// <summary>
    /// Acrescenta ao config do usuário as chaves que só existem no config empacotado,
    /// recursivamente. <b>Nunca sobrescreve valor existente</b> — o que o administrador
    /// ajustou continua valendo; o que ele nunca viu ganha o padrão da versão nova.
    /// Devolve true se o arquivo foi alterado.
    /// </summary>
    internal static bool CompletarChavesNovas(string caminhoUsuario, string caminhoPadrao)
    {
        if (!File.Exists(caminhoUsuario) || !File.Exists(caminhoPadrao)) return false;

        try
        {
            if (JsonNode.Parse(File.ReadAllText(caminhoUsuario)) is not JsonObject usuario) return false;
            if (JsonNode.Parse(File.ReadAllText(caminhoPadrao)) is not JsonObject padrao) return false;
            if (!Completar(usuario, padrao)) return false;

            File.WriteAllText(caminhoUsuario, usuario.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }));
            return true;
        }
        catch (Exception)
        {
            // Config do usuário ilegível não pode impedir o app de abrir: AppSettings.Load
            // já cai nos padrões, e a tela de configurações permite corrigir.
            return false;
        }
    }

    private static bool Completar(JsonObject usuario, JsonObject padrao)
    {
        var mudou = false;
        foreach (var (chave, valorPadrao) in padrao)
        {
            if (!usuario.TryGetPropertyValue(chave, out var valorUsuario) || valorUsuario is null)
            {
                // Reparse em vez de reaproveitar o nó: um JsonNode só pode ter um pai.
                usuario[chave] = valorPadrao is null ? null : JsonNode.Parse(valorPadrao.ToJsonString());
                mudou = true;
            }
            else if (valorUsuario is JsonObject objUsuario && valorPadrao is JsonObject objPadrao)
            {
                mudou |= Completar(objUsuario, objPadrao);
            }
        }
        return mudou;
    }

    public ListasFechadas CarregarListas() => ListasFechadas.Load(CaminhoListas);

    public string CarregarGlossario()
        => File.Exists(CaminhoGlossario) ? File.ReadAllText(CaminhoGlossario) : string.Empty;

    public void SalvarSettings(AppSettings settings)
    {
        settings.Save(CaminhoAppSettings);
        Settings = settings;
    }

    public void SalvarListas(ListasFechadas listas) => listas.Save(CaminhoListas);

    public void SalvarGlossario(string texto) => File.WriteAllText(CaminhoGlossario, texto);
}
