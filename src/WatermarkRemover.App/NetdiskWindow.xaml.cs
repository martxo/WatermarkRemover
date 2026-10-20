using System.Windows;
using System.Windows.Input;
using WatermarkRemover.App.ViewModels;

namespace WatermarkRemover.App;

/// <summary>
/// The 网盘 management window. Deliberately thin: the constructor wires the two view models and
/// the load hook, and the re-authorize button only syncs the shared account panel's selection.
/// </summary>
public partial class NetdiskWindow : Window
{
    private readonly NetdiskViewModel _viewModel;
    private readonly NetdiskAccountSettingViewModel _authPanel;

    public NetdiskWindow(NetdiskViewModel viewModel, NetdiskAccountSettingViewModel authPanel)
    {
        InitializeComponent();
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _authPanel = authPanel ?? throw new ArgumentNullException(nameof(authPanel));
        DataContext = _viewModel;
        AuthExpander.DataContext = _authPanel;
        Loaded += async (_, _) => await _viewModel.InitializeAsync();
    }

    private void OnReauthorizeClick(object sender, RoutedEventArgs e)
    {
        // Bring the shared account panel up preloaded with the account that needs re-authorization.
        _authPanel.SelectedProvider = _viewModel.SelectedProvider;
        _authPanel.SelectedAccountName = _viewModel.SelectedAccountName;
        AuthExpander.IsExpanded = true;
    }

    private void OnSearchBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _viewModel.SearchCommand.CanExecute(null))
        {
            _viewModel.SearchCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
