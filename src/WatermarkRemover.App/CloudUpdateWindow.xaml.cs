using System.Windows;
using WatermarkRemover.App.ViewModels;

namespace WatermarkRemover.App;

/// <summary>
/// 云更新中心. Behaviour lives in <see cref="CloudUpdateViewModel"/>; this window only kicks off
/// the initial check when it opens.
/// </summary>
public partial class CloudUpdateWindow : Window
{
    private readonly CloudUpdateViewModel _viewModel;

    public CloudUpdateWindow(CloudUpdateViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        Loaded += async (_, _) => await _viewModel.InitializeAsync();
    }
}
