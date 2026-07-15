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

        var settings = AppSettings.Load(Path.Combine(userDir, "appsettings.json"));
        return new ConfigService(userDir, settings);
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
