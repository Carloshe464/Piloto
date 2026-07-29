using System.Windows;
using System.Threading;

namespace Piloto.LogMonitor;

public partial class App : Application
{
    private Mutex? _mutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        _mutex = new Mutex(true, "ClickWriteLogMonitorMutex", out var primeiraInstancia);
        if (!primeiraInstancia)
        {
            Shutdown();
            return;
        }
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
