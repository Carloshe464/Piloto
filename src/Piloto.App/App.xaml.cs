using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Piloto.App.Services;
using Piloto.Bridge;
using Piloto.Core.Abstractions;
using Piloto.Core.Pipeline;

namespace Piloto.App;

public partial class App : Application
{
    private ServiceProvider? _provider;
    private ConfigService? _config;
    private TrayIconController? _tray;
    private MainWindow? _main;
    private ILogger<App>? _log;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;

        try
        {
            _config = ConfigService.Inicializar();
            _provider = CompositionRoot.Build(_config);
            _log = _provider.GetRequiredService<ILogger<App>>();

            var repo = _provider.GetRequiredService<ICallRepository>();
            repo.Inicializar();

            var coordinator = _provider.GetRequiredService<RecordingCoordinator>();
            var queue = _provider.GetRequiredService<QueueProcessor>();
            var bridge = _provider.GetRequiredService<ZendeskBridgeServer>();

            _main = _provider.GetRequiredService<MainWindow>();

            _tray = new TrayIconController(
                abrir: MostrarPrincipal,
                alternarGravacao: () => _main!.AlternarGravacao(),
                naoGravar: () => _main!.NaoGravar(),
                configuracoes: () => _main!.AbrirConfiguracoes(),
                sair: Encerrar);

            coordinator.EstadoGravacaoMudou += (_, gravando) =>
                Dispatcher.Invoke(() => _tray!.AtualizarGravacao(gravando));

            queue.RegistroProcessado += (_, reg) => Dispatcher.Invoke(() =>
            {
                _tray!.Notificar("Piloto", reg.PrecisaRevisao
                    ? "Nova transcrição pronta — precisa de revisão"
                    : "Nova transcrição pronta");
                _main!.Recarregar();
            });

            try { bridge.Iniciar(); }
            catch (Exception ex) { _log.LogError(ex, "Falha ao iniciar o bridge na porta {Porta}", _config.Settings.Bridge.Porta); }

            queue.Iniciar();

            _ = Task.Run(() => AplicarRetencao(repo));

            MostrarPrincipal();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "Falha ao iniciar o Piloto:\n\n" + ex.Message,
                "Piloto", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private void AplicarRetencao(ICallRepository repo)
    {
        try
        {
            var r = _config!.Settings.RetencaoDias;
            var resultado = repo.AplicarRetencao(r.Audio, r.Transcricao);
            _log?.LogInformation("Retenção: {Audios} áudios e {Regs} registros removidos",
                resultado.AudiosRemovidos, resultado.RegistrosRemovidos);
        }
        catch (Exception ex) { _log?.LogError(ex, "Falha ao aplicar retenção"); }
    }

    private void MostrarPrincipal()
    {
        if (_main is null) return;
        _main.Show();
        _main.WindowState = WindowState.Normal;
        _main.Activate();
    }

    private void Encerrar()
    {
        _log?.LogInformation("Encerrando o Piloto");
        Shutdown();
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _log?.LogError(e.Exception, "Exceção não tratada na UI");
        MessageBox.Show("Ocorreu um erro inesperado:\n\n" + e.Exception.Message,
            "Piloto", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _tray?.Dispose();
            _provider?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        catch { /* ignore */ }
        base.OnExit(e);
    }
}
