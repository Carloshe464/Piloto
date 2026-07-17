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
    /// <summary>Nome do mutex de instância única — o instalador (setup.iss) usa o mesmo
    /// nome em CheckForMutexes para detectar o app em execução antes de atualizar.</summary>
    private const string NomeMutex = "PilotoAppMutex";

    private ServiceProvider? _provider;
    private ConfigService? _config;
    private TrayIconController? _tray;
    private MainWindow? _main;
    private ILogger<App>? _log;
    private Mutex? _mutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _mutex = new Mutex(initiallyOwned: false, NomeMutex, out var primeiraInstancia);
        if (!primeiraInstancia)
        {
            // Duas instâncias consumiriam a mesma fila SQLite em paralelo (itens duplicados)
            // e a segunda falharia na porta do bridge. Bandeja + autostart tornam isso comum.
            MessageBox.Show("O Piloto já está em execução — procure o ícone na bandeja, ao lado do relógio.",
                "Piloto", MessageBoxButton.OK, MessageBoxImage.Information);
            _mutex.Dispose();
            _mutex = null;
            Shutdown(0);
            return;
        }

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

            coordinator.AvisoCaptura += (_, msg) =>
                Dispatcher.Invoke(() => _tray!.Notificar("Piloto — captura de áudio", msg));

            coordinator.ChamadaEnfileirada += (_, id) => Dispatcher.Invoke(() =>
                _main!.MostrarStatus($"Chamada #{id} enfileirada — captura automática pela extensão."));

            queue.ItemIniciado += (_, id) => Dispatcher.Invoke(() =>
                _main!.MostrarStatus($"Processando ligação #{id} em segundo plano — transcrição e resumo a caminho…"));

            queue.RegistroProcessado += (_, reg) => Dispatcher.Invoke(() =>
            {
                _tray!.Notificar("Piloto", reg.PrecisaRevisao
                    ? "Nova transcrição pronta — precisa de revisão"
                    : "Nova transcrição pronta");
                _main!.MostrarStatus($"Ligação #{reg.Id} pronta — transcrição e resumo disponíveis.");
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
            var arquivo = RegistrarErroInicializacao(ex);
            _log?.LogError(ex, "Falha ao iniciar o Piloto");
            MessageBox.Show(
                $"Falha ao iniciar o Piloto:\n\n[{ex.GetType().Name}] {ex.Message}\n\nDetalhes em:\n{arquivo}",
                "Piloto", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    /// <summary>Grava o stack completo do erro de inicialização em um arquivo fácil de localizar.</summary>
    private static string RegistrarErroInicializacao(Exception ex)
    {
        try
        {
            var pasta = Path.Combine(
                Environment.ExpandEnvironmentVariables("%LOCALAPPDATA%"), "Piloto", "logs");
            Directory.CreateDirectory(pasta);
            var arquivo = Path.Combine(pasta, "startup-error.txt");
            File.WriteAllText(arquivo, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}\n{ex}");
            return arquivo;
        }
        catch
        {
            return "(não foi possível gravar o arquivo de log)";
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
            // Marca encerramento limpo: sem esta linha no log, o processo morreu (crash).
            _log?.LogInformation("Piloto encerrado normalmente");
            _tray?.Dispose();
            _provider?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _mutex?.Dispose();
        }
        catch { /* ignore */ }
        base.OnExit(e);
    }
}
