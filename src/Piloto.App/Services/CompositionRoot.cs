using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Piloto.Audio;
using Piloto.Bridge;
using Piloto.Core.Abstractions;
using Piloto.Core.Configuration;
using Piloto.Core.Grounding;
using Piloto.Core.Pipeline;
using Piloto.Core.Services;
using Piloto.Core.Text;
using Piloto.Data;
using Piloto.Data.Export;
using Piloto.Llm;
using Piloto.Remote;
using Piloto.Rules;

namespace Piloto.App.Services;

/// <summary>Monta o contêiner de injeção de dependência com todas as camadas do piloto.</summary>
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

        // Provedores que releem a config editável a cada uso (refletem edições do admin).
        services.AddSingleton<Func<ListasFechadas>>(sp => () => sp.GetRequiredService<ConfigService>().CarregarListas());
        services.AddSingleton<Func<string?>>(sp => () => sp.GetRequiredService<ConfigService>().CarregarGlossario());

        // Núcleo e camadas
        services.AddSingleton<IModelCatalog, ModelCatalog>();
        services.AddSingleton<ITextNormalizer, TextNormalizer>();
        services.AddSingleton<IRuleExtractor, RuleExtractor>();
        services.AddSingleton<IGroundingChecker, GroundingChecker>();
        services.AddSingleton<ICallRepository, SqliteCallRepository>();
        services.AddSingleton<IExporter, RecordExporter>();
        services.AddSingleton(new PromptBuilder());

        // Transcrição no servidor. O WhisperTranscriber continua no repositório (histórico
        // dos filtros calibrados em campo), mas fora do contêiner: não há mais fallback
        // local — se o servidor não responder, a fila enfileira e reenvia.
        services.AddSingleton<ServidorTranscricaoClient>();
        services.AddSingleton<ServidorSaudeMonitor>();
        services.AddSingleton<ITranscriber, RemoteTranscriber>();

        services.AddSingleton<ILlmExtractor, LlmWorkerExtractor>();
        services.AddSingleton<IAudioRecorder, WasapiDualChannelRecorder>();
        services.AddSingleton<ExtensionAudioRecorder>();
        services.AddSingleton<TranscriptionPipeline>();
        services.AddSingleton<QueueProcessor>();
        services.AddSingleton<CallEnqueuer>();
        services.AddSingleton(sp => new ZendeskBridgeServer(
            settings.Bridge.Porta, sp.GetRequiredService<ILogger<ZendeskBridgeServer>>()));

        // Serviços de aplicação e janela principal
        services.AddSingleton<RecordingCoordinator>();
        services.AddSingleton<MainWindow>();

        return services.BuildServiceProvider();
    }
}
