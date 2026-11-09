using System.Windows;
using System.Windows.Controls;
using WatermarkRemover.App.ViewModels;

namespace WatermarkRemover.App;

/// <summary>
/// Thin shell for the shared 网盘 account panel; most behaviour lives in
/// <see cref="NetdiskAccountSettingViewModel"/> set as the data context by the host. The one
/// exception is the 天翼云盘 embedded-browser login, which needs a Window and therefore belongs
/// to the view.
/// </summary>
public partial class NetdiskAccountPanel : UserControl
{
    public NetdiskAccountPanel()
    {
        InitializeComponent();
    }

    private void OnTianyiLoginClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not NetdiskAccountSettingViewModel viewModel ||
            !viewModel.IsTianyi)
        {
            return;
        }

        var window = new TianyiLoginWindow
        {
            Owner = Window.GetWindow(this),
        };
        if (window.ShowDialog() == true && window.CookieHeader is { Length: > 0 } cookie)
        {
            viewModel.ApplyTianyiCookie(cookie);
        }
    }
}
