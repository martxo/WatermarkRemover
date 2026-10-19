using System.Windows.Controls;

namespace WatermarkRemover.App;

/// <summary>
/// Thin shell for the shared 网盘 account panel; all behaviour lives in
/// <see cref="ViewModels.NetdiskAccountSettingViewModel"/> set as the data context by the host.
/// </summary>
public partial class NetdiskAccountPanel : UserControl
{
    public NetdiskAccountPanel()
    {
        InitializeComponent();
    }
}
