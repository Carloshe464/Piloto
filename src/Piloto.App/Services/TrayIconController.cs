using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Hardcodet.Wpf.TaskbarNotification;

namespace Piloto.App.Services;

/// <summary>
/// Ícone de bandeja: indicador visível de gravação (regra de privacidade), menu de contexto
/// e notificações de novas transcrições.
/// </summary>
public sealed class TrayIconController : IDisposable
{
    private readonly TaskbarIcon _tray;
    private readonly MenuItem _itemGravar;
    private readonly MenuItem _itemNaoGravar;

    private static readonly ImageSource IconeOcioso = Bolinha(Color.FromRgb(0x6b, 0x72, 0x80));
    private static readonly ImageSource IconeGravando = Bolinha(Color.FromRgb(0xdc, 0x26, 0x26));

    public TrayIconController(Action abrir, Action alternarGravacao, Action naoGravar, Action configuracoes, Action sair)
    {
        _itemGravar = new MenuItem { Header = "Iniciar gravação" };
        _itemGravar.Click += (_, _) => alternarGravacao();

        _itemNaoGravar = new MenuItem { Header = "Não gravar esta chamada", IsEnabled = false };
        _itemNaoGravar.Click += (_, _) => naoGravar();

        var itemAbrir = new MenuItem { Header = "Abrir Piloto" };
        itemAbrir.Click += (_, _) => abrir();

        var itemConfig = new MenuItem { Header = "Configurações…" };
        itemConfig.Click += (_, _) => configuracoes();

        var itemSair = new MenuItem { Header = "Sair" };
        itemSair.Click += (_, _) => sair();

        var menu = new ContextMenu();
        menu.Items.Add(itemAbrir);
        menu.Items.Add(new Separator());
        menu.Items.Add(_itemGravar);
        menu.Items.Add(_itemNaoGravar);
        menu.Items.Add(new Separator());
        menu.Items.Add(itemConfig);
        menu.Items.Add(itemSair);

        _tray = new TaskbarIcon
        {
            ToolTipText = "Piloto — pronto",
            IconSource = IconeOcioso,
            ContextMenu = menu,
        };
        _tray.TrayMouseDoubleClick += (_, _) => abrir();
    }

    public void AtualizarGravacao(bool gravando)
    {
        _tray.IconSource = gravando ? IconeGravando : IconeOcioso;
        _tray.ToolTipText = gravando ? "Piloto — GRAVANDO" : "Piloto — pronto";
        _itemGravar.Header = gravando ? "Parar e transcrever" : "Iniciar gravação";
        _itemNaoGravar.IsEnabled = gravando;
    }

    public void Notificar(string titulo, string mensagem)
        => _tray.ShowBalloonTip(titulo, mensagem, BalloonIcon.Info);

    private static ImageSource Bolinha(Color cor)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
            dc.DrawEllipse(new SolidColorBrush(cor), null, new System.Windows.Point(16, 16), 13, 13);

        var rtb = new RenderTargetBitmap(32, 32, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }

    public void Dispose() => _tray.Dispose();
}
