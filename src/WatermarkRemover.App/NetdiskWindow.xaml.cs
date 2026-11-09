using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
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
    private bool _syncingSelection;

    public NetdiskWindow(NetdiskViewModel viewModel, NetdiskAccountSettingViewModel authPanel)
    {
        InitializeComponent();
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _authPanel = authPanel ?? throw new ArgumentNullException(nameof(authPanel));
        DataContext = _viewModel;
        AuthExpander.DataContext = _authPanel;

        // The toolbar and the re-authorize panel are two views of the same choice. Without this
        // they kept independent selections, so the panel could show another product than the one
        // being browsed — the "layout is a mess / switching did nothing" report.
        _viewModel.PropertyChanged += OnListingPropertyChanged;
        _authPanel.PropertyChanged += OnAuthPanelPropertyChanged;
        Closed += (_, _) =>
        {
            _viewModel.PropertyChanged -= OnListingPropertyChanged;
            _authPanel.PropertyChanged -= OnAuthPanelPropertyChanged;
        };

        Loaded += async (_, _) => await _viewModel.InitializeAsync();
    }

    /// <summary>
    /// Switches the listing to an account saved elsewhere (the 设置 page) or by the panel inside
    /// this window. The coordinator owns the AccountSaved subscription so both entry points behave
    /// the same — see <c>NetdiskCoordinator.OnAccountSaved</c>.
    /// </summary>
    internal Task ShowAccountAsync(string providerId, string accountName) =>
        _viewModel.SelectAccountAsync(providerId, accountName);

    private void OnListingPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_syncingSelection)
        {
            return;
        }

        switch (e.PropertyName)
        {
            case nameof(NetdiskViewModel.SelectedProvider):
                SyncSelection(() => _authPanel.SelectedProvider = _viewModel.SelectedProvider);
                break;
            case nameof(NetdiskViewModel.SelectedAccountName):
                SyncSelection(() => _authPanel.SelectedAccountName = _viewModel.SelectedAccountName);
                break;
        }
    }

    private void OnAuthPanelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_syncingSelection)
        {
            return;
        }

        switch (e.PropertyName)
        {
            case nameof(NetdiskAccountSettingViewModel.SelectedProvider):
                SyncSelection(() => _viewModel.SelectedProvider = _authPanel.SelectedProvider);
                break;
            case nameof(NetdiskAccountSettingViewModel.SelectedAccountName):
                SyncSelection(() => _viewModel.SelectedAccountName = _authPanel.SelectedAccountName);
                break;
        }
    }

    private void SyncSelection(Action apply)
    {
        _syncingSelection = true;
        try
        {
            apply();
        }
        finally
        {
            _syncingSelection = false;
        }
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

    /// <summary>Projects the DataGrid's extended selection into the view model.</summary>
    private void OnFileSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is NetdiskViewModel vm && sender is DataGrid grid)
        {
            vm.SetSelection(grid.SelectedItems.OfType<NetdiskItemViewModel>());
        }
    }

    private void OnSelectAllClick(object sender, RoutedEventArgs e) => FileGrid.SelectAll();

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
