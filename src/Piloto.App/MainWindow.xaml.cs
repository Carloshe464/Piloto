using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Piloto.App.Services;
using Piloto.App.ViewModels;
using Piloto.App.Views;
using Piloto.Core.Abstractions;
using Piloto.Remote;

namespace Piloto.App;

public partial class MainWindow : Window
{
    private readonly ICallRepository _repo;
    private readonly IExporter _exporter;
    private readonly IModelCatalog _modelos;
    private readonly ServidorSaudeMonitor _servidor;
    private readonly RecordingCoordinator _coordinator;
    private readonly Core.Services.CallEnqueuer _enqueuer;
    private readonly ConfigService _config;
    private readonly ILogger<MainWindow> _log;

    private readonly ObservableCollection<CallRowVm> _linhas = new();
    private readonly DispatcherTimer _timer;

    public MainWindow(
        ICallRepository repo,
        IExporter exporter,
        IModelCatalog modelos,
        ServidorSaudeMonitor servidor,
        RecordingCoordinator coordinator,
        Core.Services.CallEnqueuer enqueuer,
        ConfigService config,
        ILogger<MainWindow> log)
    {
        _repo = repo;
        _exporter = exporter;
        _modelos = modelos;
        _servidor = servidor;
        _coordinator = coordinator;
        _enqueuer = enqueuer;
        _config = config;
        _log = log;

        InitializeComponent();

        ListaChamadas.ItemsSource = _linhas;
        _coordinator.EstadoGravacaoMudou += (_, gravando) => Dispatcher.Invoke(() => AtualizarBotoes(gravando));
        _servidor.Mudou += (_, _) => Dispatcher.Invoke(AtualizarBanner);

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _timer.Tick += (_, _) => AtualizarContadores();
        _timer.Start();

        Loaded += (_, _) =>
        {
            AtualizarBanner();
            AtualizarBotoes(_coordinator.EstaGravando);
            Recarregar();
        };
    }

    // -------------------------------------------------------- API p/ App/bandeja

    public void AlternarGravacao()
    {
        try
        {
            if (_coordinator.EstaGravando)
            {
                var id = _coordinator.PararEEnfileirar();
                TxtStatus.Text = $"Chamada enfileirada (#{id}). Será transcrita em segundo plano.";
            }
            else
            {
                _coordinator.Iniciar();
                TxtStatus.Text = "Gravando… clique em Parar para transcrever.";
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Falha ao alternar gravação");
            MessageBox.Show("Não foi possível acessar o áudio:\n\n" + ex.Message,
                "Click Write", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        AtualizarContadores();
    }

    public void NaoGravar()
    {
        if (!_coordinator.EstaGravando) return;
        _coordinator.Descartar();
        TxtStatus.Text = "Gravação descartada — nada foi salvo.";
    }

    public void AbrirConfiguracoes()
    {
        var win = new SettingsWindow(_config, _modelos, _servidor) { Owner = this };
        win.ShowDialog();
        AtualizarBanner();
    }

    /// <summary>Atualiza a linha de status (usada pelo App para refletir a fila).</summary>
    public void MostrarStatus(string texto) => TxtStatus.Text = texto;

    public void Recarregar()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(Recarregar); return; }

        var termo = TxtBusca.Text?.Trim() ?? "";
        var registros = string.IsNullOrEmpty(termo) ? _repo.ListarRegistros() : _repo.Buscar(termo);

        _linhas.Clear();
        foreach (var r in registros) _linhas.Add(CallRowVm.De(r));
        AtualizarContadores();
    }

    // ---------------------------------------------------------------- Handlers

    private void BtnGravar_Click(object sender, RoutedEventArgs e) => AlternarGravacao();
    private void BtnNaoGravar_Click(object sender, RoutedEventArgs e) => NaoGravar();
    private void BtnConfig_Click(object sender, RoutedEventArgs e) => AbrirConfiguracoes();
    private void BtnAtualizar_Click(object sender, RoutedEventArgs e) => Recarregar();
    private void BtnBuscar_Click(object sender, RoutedEventArgs e) => Recarregar();

    private void TxtBusca_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Recarregar();
    }

    private void Lista_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ListaChamadas.SelectedItem is not CallRowVm linha) return;
        var registro = _repo.ObterRegistro(linha.Id);
        if (registro is null) return;

        var win = new DetailWindow(registro, _exporter, _enqueuer) { Owner = this };
        win.ShowDialog();
        Recarregar(); // um reprocessamento pode ter sido enfileirado no detalhe
    }

    // ---------------------------------------------------------------- Interno

    private void AtualizarBotoes(bool gravando)
    {
        BtnGravar.Content = gravando ? "■ Parar e transcrever" : "● Iniciar gravação";
        BtnNaoGravar.IsEnabled = gravando;
    }

    /// <summary>
    /// Avisos do topo. A regra: o atendente precisa saber, em uma frase, se a ligação que
    /// ele acabou de gravar <b>está guardada</b> — "servidor fora do ar" sem essa garantia
    /// é indistinguível de "a ligação se perdeu".
    /// </summary>
    public void AtualizarBanner()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(AtualizarBanner); return; }

        var avisos = new List<string>();

        if (_servidor.UltimoErro is { } erro)
        {
            avisos.Add($"Servidor de transcrição indisponível ({_servidor.Endereco}): {erro} — "
                       + "as ligações continuam sendo gravadas e serão enviadas quando ele voltar. Nada se perde.");
        }
        else if (_servidor.Ultima is { } saude)
        {
            if (!saude.ContratoCompativel)
                avisos.Add($"O servidor fala o contrato {saude.VersaoContrato ?? "?"} e este app conhece o "
                           + $"{Core.Models.ServidorSaude.ContratoSuportado}: a transcrição continua, mas os campos e o resumo do servidor serão ignorados.");
            else if (!saude.ModeloCarregado)
                avisos.Add("O servidor está de pé, mas ainda carregando o modelo — as primeiras ligações podem demorar mais.");
        }

        var ausentes = _modelos.ModelosAusentes();
        if (ausentes.Count > 0)
            avisos.Add($"Modelo de resumo ausente ({string.Join(", ", ausentes)}). As ligações são transcritas normalmente; "
                       + "o resumo fica pendente até baixá-lo (scripts/download-models.ps1).");

        if (avisos.Count == 0)
        {
            BannerModelos.Visibility = Visibility.Collapsed;
            return;
        }

        TxtBanner.Text = string.Join("\n", avisos);
        BannerModelos.Visibility = Visibility.Visible;
    }

    private void AtualizarContadores()
    {
        try
        {
            var c = _repo.Contadores();
            var fila = _repo.ContarPendentes();
            TxtTotal.Text = $"Chamadas: {c.TotalChamadas}";
            TxtTempo.Text = $"Tempo falado: {c.TempoTotalFalado:hh\\:mm\\:ss}";
            TxtRevisao.Text = $"A revisar: {c.PendentesRevisao}";
            TxtFila.Text = $"Fila: {fila}";
        }
        catch (Exception ex) { _log.LogDebug(ex, "Falha ao atualizar contadores"); }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Fechar a janela apenas esconde para a bandeja; sair é pela opção "Sair".
        e.Cancel = true;
        Hide();
        base.OnClosing(e);
    }
}
