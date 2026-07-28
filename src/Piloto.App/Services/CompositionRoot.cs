using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Piloto.Audio;
using Piloto.Bridge;
using Piloto.Core.Abstractions;
using Piloto.Core.Configuration;
using Piloto.Core.Services;
using Piloto.Core.Text;
using Piloto.Data;
using Piloto.Data.Export;

namespace Piloto.App.Services;

/// <summary>
/// Monta o contêiner de injeção de dependência.
/// <para>
/// A partir da 1.1 o aplicativo não transcreve nem resume: ele captura os dois canais,
/// envia ao servidor e grava de volta o resultado. Saíram daqui o transcritor, o
/// extrator de LLM, as regras, o pipeline e a fila local — e com eles as bibliotecas
/// nativas do Whisper e do llama.cpp.
/// </para>
/// </summary>
public static class CompositionRoot
{
    public static ServiceProvider Build(ConfigService config)
    {
        var services = new ServiceCollection();
        var settings = config.Settings;

        services.AddSingleton(config);
        services.AddSingleton(settings);

        services.AddLogging(b =>
        {
            b.SetMinimumLevel(LogLevel.Information);
            b.AddProvider(new FileLoggerProvider(settings.PastaDadosExpandida));
        });

        // Listas fechadas continuam sendo lidas da config para a tela montar seletores;
        // quem escolhe dentro delas agora é o servidor.
        services.AddSingleton<Func<ListasFechadas>>(sp =>
            () => sp.GetRequiredService<ConfigService>().CarregarListas());

        // Persistência e exportação: o banco local virou espelho do servidor.
        services.AddSingleton<ITextNormalizer, TextNormalizer>();
        services.AddSingleton<ICallRepository, SqliteCallRepository>();
        services.AddSingleton<IExporter, RecordExporter>();

        // Captura
        services.AddSingleton<IAudioRecorder, WasapiDualChannelRecorder>();
        services.AddSingleton<ExtensionAudioRecorder>();
        services.AddSingleton(sp => new ZendeskBridgeServer(
            settings.Bridge.Porta, sp.GetRequiredService<ILogger<ZendeskBridgeServer>>()));

        // Servidor de transcrição
        services.AddSingleton<ClickWriteUploader>();
        services.AddSingleton<SincronizadorServidor>();

        services.AddSingleton<RecordingCoordinator>();
        services.AddSingleton<MainWindow>();

        return services.BuildServiceProvider();
    }
}
