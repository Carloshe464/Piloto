using System.Diagnostics;
using System.Globalization;
using System.Windows;
using Piloto.App.Services;
using Piloto.Core.Abstractions;
using Piloto.Core.Configuration;

namespace Piloto.App.Views;

public partial class SettingsWindow : Window
{
    private readonly ConfigService _config;
    private readonly IModelCatalog _modelos;

    public SettingsWindow(ConfigService config, IModelCatalog modelos)
    {
        _config = config;
        _modelos = modelos;
        InitializeComponent();
        Carregar();
    }

    private void Carregar()
    {
        var s = _config.Settings;
        var listas = _config.CarregarListas();

        TxtMotivos.Text = string.Join(Environment.NewLine, listas.MotivoContato);
        TxtProdutos.Text = string.Join(Environment.NewLine, listas.Produto);
        TxtStatus.Text = string.Join(Environment.NewLine, listas.Status);
        TxtGlossario.Text = _config.CarregarGlossario();

        TxtRetAudio.Text = s.RetencaoDias.Audio.ToString(CultureInfo.InvariantCulture);
        TxtRetTransc.Text = s.RetencaoDias.Transcricao.ToString(CultureInfo.InvariantCulture);
        TxtPorta.Text = s.Bridge.Porta.ToString(CultureInfo.InvariantCulture);

        TxtWhisperModelo.Text = s.Whisper.Modelo;
        TxtWhisperThreads.Text = s.Whisper.Threads.ToString(CultureInfo.InvariantCulture);

        ChkLlm.IsChecked = s.Llm.Habilitado;
        TxtLlmModelo.Text = s.Llm.Modelo;
        TxtLlmThreads.Text = s.Llm.Threads.ToString(CultureInfo.InvariantCulture);
        TxtLlmTemp.Text = s.Llm.Temperatura.ToString(CultureInfo.InvariantCulture);

        AtualizarStatusModelos();
    }

    private void AtualizarStatusModelos()
    {
        var whisper = _modelos.WhisperDisponivel ? "presente" : "AUSENTE";
        var llm = _modelos.LlmDisponivel ? "presente" : "AUSENTE";
        TxtStatusModelos.Text =
            $"Pasta de modelos: {_config.Settings.PastaModelos}\n" +
            $"Whisper: {whisper}   •   LLM: {llm}\n" +
            (_modelos.PipelinePronto ? "Pipeline pronto." : "Pipeline pausado — baixe/aponte os modelos.");
    }

    private void AbrirPastaModelos_Click(object sender, RoutedEventArgs e)
    {
        var pasta = _config.Settings.PastaModelos;
        try
        {
            Directory.CreateDirectory(pasta);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{pasta}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show("Não foi possível abrir a pasta:\n" + ex.Message, "Piloto",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Salvar_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var listas = new ListasFechadas
            {
                MotivoContato = Linhas(TxtMotivos.Text),
                Produto = Linhas(TxtProdutos.Text),
                Status = Linhas(TxtStatus.Text),
            };
            _config.SalvarListas(listas);
            _config.SalvarGlossario(TxtGlossario.Text);

            var s = _config.Settings;
            s.RetencaoDias.Audio = ParseInt(TxtRetAudio.Text, s.RetencaoDias.Audio);
            s.RetencaoDias.Transcricao = ParseInt(TxtRetTransc.Text, s.RetencaoDias.Transcricao);
            s.Bridge.Porta = ParseInt(TxtPorta.Text, s.Bridge.Porta);
            s.Whisper.Modelo = TxtWhisperModelo.Text.Trim();
            s.Whisper.Threads = ParseInt(TxtWhisperThreads.Text, s.Whisper.Threads);
            s.Llm.Habilitado = ChkLlm.IsChecked == true;
            s.Llm.Modelo = TxtLlmModelo.Text.Trim();
            s.Llm.Threads = ParseInt(TxtLlmThreads.Text, s.Llm.Threads);
            s.Llm.Temperatura = ParseFloat(TxtLlmTemp.Text, s.Llm.Temperatura);
            _config.SalvarSettings(s);

            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show("Falha ao salvar:\n\n" + ex.Message, "Piloto",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Cancelar_Click(object sender, RoutedEventArgs e) => Close();

    private static List<string> Linhas(string texto)
        => texto.Split('\n')
                .Select(l => l.Trim().TrimEnd('\r'))
                .Where(l => l.Length > 0)
                .ToList();

    private static int ParseInt(string texto, int fallback)
        => int.TryParse(texto.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private static float ParseFloat(string texto, float fallback)
        => float.TryParse(texto.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
}
