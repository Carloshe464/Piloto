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
    private readonly Core.Services.ClickWriteUploader _uploader;

    public SettingsWindow(ConfigService config, Core.Services.ClickWriteUploader uploader)
    {
        _config = config;
        _uploader = uploader;
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

    /// <summary>
    /// O painel que mostrava se os modelos locais estavam presentes agora mostra se o
    /// servidor está respondendo. É a mesma pergunta de sempre — "dá para processar?" —
    /// e a resposta mudou de lugar junto com a inferência.
    /// </summary>
    private async void AtualizarStatusModelos()
    {
        var s = _config.Settings.Servidor;
        var comToken = string.IsNullOrWhiteSpace(s.Token) ? "sem token" : "token configurado";
        TxtStatusModelos.Text = $"Servidor: {s.Url}\n{comToken}\nverificando…";

        var noAr = await _uploader.ServidorNoArAsync();
        var retidas = _uploader.PendentesEmDisco();

        TxtStatusModelos.Text =
            $"Servidor: {s.Url}\n" +
            $"{comToken}   •   {(noAr ? "respondendo" : "INACESSÍVEL")}\n" +
            (retidas > 0
                ? $"{retidas} ligação(ões) guardada(s) nesta máquina, aguardando envio."
                : "Nada pendente de envio.");
    }

    private void AbrirPastaModelos_Click(object sender, RoutedEventArgs e)
    {
        // O botão passou a abrir a pasta de dados: é onde ficam as ligações retidas
        // (pendentes) e as que aguardam resultado — o que alguém precisa inspecionar
        // quando algo não chega ao servidor. Modelos não existem mais nesta máquina.
        var pasta = _config.Settings.PastaDadosExpandida;
        try
        {
            Directory.CreateDirectory(pasta);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{pasta}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show("Não foi possível abrir a pasta:\n" + ex.Message, "Click Write",
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
            MessageBox.Show("Falha ao salvar:\n\n" + ex.Message, "Click Write",
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
